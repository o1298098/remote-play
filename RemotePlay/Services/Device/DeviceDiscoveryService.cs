using RemotePlay.Contracts.Enums;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.PlayStation;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlay.Services.Device
{

    public class DeviceDiscoveryService : IDeviceDiscoveryService
    {
        private readonly ILogger<DeviceDiscoveryService> _logger;
        private const int PS4_DISCOVERY_PORT = 987;
        private const string PS4_PROTOCOL_VERSION = "00020020";
        private const int PS5_DISCOVERY_PORT = 9302;
        private const string PS5_PROTOCOL_VERSION = "00030010";
        // 先尝试 9303..9319，都被占用时用随机端口，避免并发扫描/探测互相抢端口
        private const int LOCAL_PORT_MIN = 9303;
        private const int LOCAL_PORT_MAX = 9319;
        // UDP 无确认，扫描窗口内按此间隔重发 SRCH
        private const int SEARCH_RESEND_INTERVAL_MS = 500;
        // Windows 上 UDP 收到 ICMP 端口不可达后，下一次 Receive 会抛 ConnectionReset，需要关掉
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        private static readonly Regex StatusLineRegex = new(@"^HTTP/1\.1\s+(?<code>\d+)\s+(?<status>.+)$", RegexOptions.Compiled);

        public DeviceDiscoveryService(ILogger<DeviceDiscoveryService> logger)
        {
            _logger = logger;
        }

        public async Task<List<ConsoleInfo>> DiscoverDevicesAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
        {
            try
            {
                var targets = GetBroadcastAddresses();
                targets.Add(IPAddress.Broadcast);

                var devices = await SearchAsync(targets, timeoutMs, onlyFrom: null, cancellationToken);
                return devices.ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device discovery failed");
                throw;
            }
        }

        public async Task<ConsoleInfo?> DiscoverDeviceAsync(
            string hostIp,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(hostIp, out var hostAddress))
                throw new ArgumentException($"无效的IP地址: {hostIp}");

            try
            {
                var devices = await SearchAsync(new List<IPAddress> { hostAddress }, timeoutMs, onlyFrom: hostAddress, cancellationToken);
                var deviceInfo = devices.FirstOrDefault();

                if (deviceInfo is null)
                {
                    // 主机关机/离线时属于正常情况，由 DeviceStatusUpdateService 汇总判断离线
                    _logger.LogDebug("No discovery response from {HostIp} within timeout", hostIp);
                }

                return deviceInfo;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover device {HostIp}", hostIp);
                throw;
            }
        }

        public async Task<bool> WakeUpDeviceAsync(
            string host,
            string credential,
            string hostType,
            CancellationToken cancellationToken = default)
        {
            var isPs5 = string.Equals(hostType, "PS5", StringComparison.OrdinalIgnoreCase);
            var targetPort = isPs5 ? PS5_DISCOVERY_PORT : PS4_DISCOVERY_PORT;

            try
            {
                var message = CreateWakeUpRequest(FormatRegistKey(credential), isPs5 ? PS5_PROTOCOL_VERSION : PS4_PROTOCOL_VERSION);

                // 先直接发给主机，再向各网段广播（主机待机期间 IP 可能已变化）
                var targets = new List<IPAddress>();
                if (IPAddress.TryParse(host, out var hostAddress))
                {
                    targets.Add(hostAddress);
                }
                targets.AddRange(GetBroadcastAddresses());

                using var client = CreateDiscoveryClient();
                var sent = 0;
                foreach (var target in targets)
                {
                    if (await TrySendAsync(client, message, new IPEndPoint(target, targetPort), cancellationToken))
                    {
                        sent++;
                    }
                }

                if (sent == 0)
                {
                    _logger.LogWarning("Failed to send WAKEUP message to {Host}:{Port} on any target", host, targetPort);
                }
                return sent > 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WAKEUP message to {Host}:{Port}", host, targetPort);
                return false;
            }
        }

        /// <summary>
        /// 用一个 socket 同时向 PS4（987）和 PS5（9302）发送 SRCH，并在整个窗口内周期重发，
        /// 按 host-id 去重收集响应
        /// </summary>
        private async Task<ICollection<ConsoleInfo>> SearchAsync(
            IReadOnlyCollection<IPAddress> targets,
            int timeoutMs,
            IPAddress? onlyFrom,
            CancellationToken cancellationToken)
        {
            var found = new Dictionary<string, ConsoleInfo>(StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0)
            {
                return found.Values;
            }

            var ps4Request = CreateDiscoveryRequest(PS4_PROTOCOL_VERSION);
            var ps5Request = CreateDiscoveryRequest(PS5_PROTOCOL_VERSION);

            using var client = CreateDiscoveryClient();
            var stopwatch = Stopwatch.StartNew();
            long nextSendMs = 0;

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds >= nextSendMs)
                {
                    foreach (var target in targets)
                    {
                        await TrySendAsync(client, ps4Request, new IPEndPoint(target, PS4_DISCOVERY_PORT), cancellationToken);
                        await TrySendAsync(client, ps5Request, new IPEndPoint(target, PS5_DISCOVERY_PORT), cancellationToken);
                    }
                    nextSendMs += SEARCH_RESEND_INTERVAL_MS;
                }

                var waitMs = Math.Min(nextSendMs, timeoutMs) - stopwatch.ElapsedMilliseconds;
                if (waitMs <= 0)
                {
                    continue;
                }

                UdpReceiveResult result;
                using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveCts.CancelAfter(TimeSpan.FromMilliseconds(waitMs));
                    try
                    {
                        result = await client.ReceiveAsync(receiveCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogDebug(ex, "Discovery receive failed");
                        break;
                    }
                }

                if (onlyFrom != null && !result.RemoteEndPoint.Address.Equals(onlyFrom))
                {
                    continue;
                }

                var deviceInfo = ParseDeviceResponse(result.Buffer, result.RemoteEndPoint);
                if (deviceInfo == null)
                {
                    continue;
                }

                found[deviceInfo.Uuid] = deviceInfo;
                if (onlyFrom != null)
                {
                    break;
                }
            }

            return found.Values;
        }

        private UdpClient CreateDiscoveryClient()
        {
            for (var port = LOCAL_PORT_MIN; ; port = port == LOCAL_PORT_MAX ? 0 : port + 1)
            {
                try
                {
                    var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                    client.EnableBroadcast = true;
                    if (OperatingSystem.IsWindows())
                    {
                        client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
                    }
                    return client;
                }
                catch (SocketException) when (port != 0)
                {
                    _logger.LogDebug("Discovery failed to bind port {Port}, trying next", port);
                }
            }
        }

        private async Task<bool> TrySendAsync(UdpClient client, byte[] data, IPEndPoint target, CancellationToken cancellationToken)
        {
            try
            {
                await client.SendAsync(data, target, cancellationToken);
                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Discovery failed to send to {Target}", target);
                return false;
            }
        }

        private List<IPAddress> GetBroadcastAddresses()
        {
            var addresses = new List<IPAddress>();
            foreach (var networkInterface in GetActiveNetworkInterfaces())
            {
                foreach (var address in GetUnicastAddresses(networkInterface))
                {
                    var broadcast = CalculateBroadcastAddress(address.Address, address.IPv4Mask);
                    if (broadcast != null && !addresses.Contains(broadcast))
                    {
                        addresses.Add(broadcast);
                    }
                }
            }
            return addresses;
        }

        private List<NetworkInterface> GetActiveNetworkInterfaces()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                           ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                           ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                           !IsVirtualInterface(ni))
                .ToList();
        }

        private bool IsVirtualInterface(NetworkInterface networkInterface)
        {
            var description = networkInterface.Description.ToLowerInvariant();
            var name = networkInterface.Name.ToLowerInvariant();
            var virtualKeywords = new[]
            {
                "virtual",
                "vmware",
                "loopback",
                "hyper-v",
                "npcap",
                "docker",
                "container",
                "wan miniport",
                "isatap",
                "teredo"
            };

            return virtualKeywords.Any(keyword => description.Contains(keyword) || name.Contains(keyword));
        }

        private List<UnicastIPAddressInformation> GetUnicastAddresses(NetworkInterface networkInterface)
        {
            var addresses = new List<UnicastIPAddressInformation>();
            var ipProperties = networkInterface.GetIPProperties();

            foreach (var address in ipProperties.UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                var mask = address.IPv4Mask;
                if (mask is null)
                {
                    continue;
                }

                var maskBytes = mask.GetAddressBytes();
                if (maskBytes.All(b => b == 0))
                {
                    continue;
                }

                addresses.Add(address);
            }

            return addresses;
        }

        private byte[] GetDDPMessage(DDPMsgType ddpType, string protocolVersion, Dictionary<string, string>? data = null)
        {
            var request = $"{ddpType} * HTTP/1.1\n";
            if (data != null)
                foreach (var k in data)
                    request = request + $"{k.Key}:{k.Value}\n";
            request = request + $"device-discovery-protocol-version:{protocolVersion}\n";
            return Encoding.UTF8.GetBytes(request);
        }

        private byte[] CreateDiscoveryRequest(string protocolVersion) => GetDDPMessage(DDPMsgType.SRCH, protocolVersion);

        private byte[] CreateWakeUpRequest(string credential, string protocolVersion)
        {
            var data = new Dictionary<string, string>
            {
                { "user-credential", credential},
                { "client-type", "vr" },
                { "auth-type","R" },
                { "model", "w" },
                { "app-type", "r" },
            };
            return GetDDPMessage(DDPMsgType.WAKEUP, protocolVersion, data);
        }

        private IPAddress? CalculateBroadcastAddress(IPAddress localAddress, IPAddress? subnetMask)
        {
            if (subnetMask is null)
            {
                return null;
            }

            var addressBytes = localAddress.GetAddressBytes();
            var maskBytes = subnetMask.GetAddressBytes();

            if (addressBytes.Length != maskBytes.Length)
            {
                return null;
            }

            if (maskBytes.All(b => b == 0))
            {
                return null;
            }

            var broadcastBytes = new byte[addressBytes.Length];
            for (var i = 0; i < addressBytes.Length; i++)
            {
                broadcastBytes[i] = (byte)(addressBytes[i] | (maskBytes[i] ^ 0xFF));
            }

            return new IPAddress(broadcastBytes);
        }

        private ConsoleInfo? ParseDeviceResponse(byte[] buffer, IPEndPoint remoteEndPoint)
        {
            if (buffer is null or { Length: 0 }) return null;

            try
            {
                // 主机名可能含非 ASCII 字符（如中文），需按 UTF-8 解析
                var response = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                string name = "Unknown";
                string? hostId = null;
                string? hostType = null;
                string? systemVersion = null;
                string? status = null;
                string? deviceDiscoveryProtocolVersion = null;
                int? hostRequestPort = null;
                string? runningAppTitleId = null;
                string? runningAppName = null;
                foreach (var line in lines)
                {
                    var match = StatusLineRegex.Match(line);
                    if (match.Success)
                    {
                        status = match.Groups["status"].Value;
                        continue;
                    }
                    var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2) continue;
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "host-name": name = parts[1]; break;
                        case "host-id": hostId = parts[1]; break;
                        case "host-type": hostType = parts[1]; break;
                        case "system-version": systemVersion = parts[1]; break;
                        case "device-discovery-protocol-version": deviceDiscoveryProtocolVersion = parts[1]; break;
                        case "host-request-port":
                            if (int.TryParse(parts[1], out var port)) hostRequestPort = port;
                            break;
                        case "running-app-titleid": runningAppTitleId = parts[1]; break;
                        case "running-app-name": runningAppName = parts[1]; break;
                    }
                }

                return string.IsNullOrEmpty(hostId)
                    ? null
                    : new ConsoleInfo(
                        remoteEndPoint.Address.ToString(),
                        name,
                        hostId!,
                        hostType,
                        systemVersion,
                        deviceDiscoveryProtocolVersion,
                        status,
                        hostRequestPort,
                        runningAppTitleId,
                        runningAppName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse device discovery response");
                return null;
            }
        }

        private string FormatRegistKey(string registKey)
        {
            byte[] firstDecode = HexStringToBytes(registKey);
            string decodedString = Encoding.UTF8.GetString(firstDecode);
            byte[] secondDecode = HexStringToBytes(decodedString);
            if (secondDecode.Length > 0 && (secondDecode[0] & 0x80) != 0)
            {
                byte[] temp = new byte[secondDecode.Length + 1];
                temp[0] = 0x00;
                Array.Copy(secondDecode, 0, temp, 1, secondDecode.Length);
                secondDecode = temp;
            }

            BigInteger number = new BigInteger(secondDecode, isBigEndian: true);

            return number.ToString();
        }

        private static byte[] HexStringToBytes(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex string must have an even length");

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
