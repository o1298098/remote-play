using RemotePlay.Contracts.Enums;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.PlayStation;
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
        private const string BROADCAST_IP = "255.255.255.255";
        private const int DISCOVERY_PORT = 9302;
        private const int CLIENT_PORT = 9303;
        private const string DISCOVERY_PROTOCOL_VERSION = "00030010";
        private const int PS4_DDP_PORT = 987;
        private const int PS5_DDP_PORT = 9302;
        private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);

        public DeviceDiscoveryService(ILogger<DeviceDiscoveryService> logger)
        {
            _logger = logger;
        }

        public async Task<List<ConsoleInfo>> DiscoverDevicesAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
        {
            var discoveredDevices = new List<ConsoleInfo>();
            var discoveryTasks = new List<Task<List<ConsoleInfo>>>();

            try
            {
                var networkInterfaces = GetActiveNetworkInterfaces();

                foreach (var networkInterface in networkInterfaces)
                {
                    var unicastAddresses = GetUnicastAddresses(networkInterface);
                    foreach (var address in unicastAddresses)
                    {
                        var broadcast = CalculateBroadcastAddress(address.Address, address.IPv4Mask);
                        if (broadcast is null)
                        {
                            continue;
                        }

                        var task = DiscoverOnNetworkAsync(address.Address, broadcast, timeoutMs, cancellationToken);
                        discoveryTasks.Add(task);
                    }
                }

                var results = await Task.WhenAll(discoveryTasks);
                discoveredDevices = results.SelectMany(x => x).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备发现过程中发生错误");
                throw;
            }

            return discoveredDevices;
        }

        public async Task<ConsoleInfo?> DiscoverDeviceAsync(
            string hostIp,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = CreateDiscoveryRequest();
                var response = await SendUdpRequestAsync(
                    request, hostIp,
                    DISCOVERY_PORT, timeoutMs,
                    receiveData: true,
                    cancellationToken: cancellationToken);

                if (response is null)
                {
                    _logger.LogWarning("在指定时间内未发现设备: {HostIp}", hostIp);
                    return null;
                }

                var deviceInfo = ParseDeviceResponse(response, new IPEndPoint(IPAddress.Parse(hostIp), DISCOVERY_PORT));

                if (deviceInfo != null)
                {
                    return deviceInfo;
                }

                _logger.LogWarning("收到无效响应: {HostIp}", hostIp);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发现设备时发生错误: {HostIp}", hostIp);
                throw;
            }
        }

        public async Task<bool> WakeUpDeviceAsync(
            string host,
            string credential,
            string hostType,
            CancellationToken cancellationToken = default)
        {
            var targetPort = hostType == "PS5" ? PS5_DDP_PORT : PS4_DDP_PORT;
            var _c = FormatRegistKey(credential);
            var message = CreateWakeUpRequest(_c);

            try
            {
                var networkInterfaces = GetActiveNetworkInterfaces();

                foreach (var networkInterface in networkInterfaces)
                {
                    var unicastAddresses = GetUnicastAddresses(networkInterface);
                    foreach (var address in unicastAddresses)
                    {
                        var broadcast = CalculateBroadcastAddress(address.Address, address.IPv4Mask);
                        if (broadcast is null)
                        {
                            continue;
                        }

                        await SendUdpRequestAsync(message, broadcast.ToString(), targetPort, receiveData: false, cancellationToken: cancellationToken);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send WAKEUP message to {Host}:{Port}", host, targetPort);
                return false;
            }
        }

        private async Task<List<ConsoleInfo>> DiscoverOnNetworkAsync(IPAddress localAddress, IPAddress broadcastAddress, int timeoutMs, CancellationToken cancellationToken)
        {
            var discoveredDevices = new List<ConsoleInfo>();

            var lockTaken = false;

            try
            {
                await _discoverySemaphore.WaitAsync(cancellationToken);
                lockTaken = true;

                var request = CreateDiscoveryRequest();
                var broadcastEndPoint = new IPEndPoint(broadcastAddress, DISCOVERY_PORT);
                var localEndPoint = new IPEndPoint(localAddress, CLIENT_PORT);

                using var client = new UdpClient(localEndPoint);
                client.EnableBroadcast = true;

                await client.SendAsync(request, request.Length, broadcastEndPoint);

                var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var receiveTask = client.ReceiveAsync();
                        var delayTask = Task.Delay(Math.Max(0, (int)(endTime - DateTime.UtcNow).TotalMilliseconds), cancellationToken);
                        var completed = await Task.WhenAny(receiveTask, delayTask);

                        if (completed == receiveTask)
                        {
                            var result = receiveTask.Result;
                            var deviceInfo = ParseDeviceResponse(result.Buffer, result.RemoteEndPoint);
                            if (deviceInfo != null && !discoveredDevices.Any(d => d.Uuid == deviceInfo.Uuid))
                            {
                                discoveredDevices.Add(deviceInfo);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在网络接口 {LocalAddress} 上发现设备时发生错误", localAddress);
            }
            finally
            {
                if (lockTaken)
                {
                    _discoverySemaphore.Release();
                }
            }

            return discoveredDevices;
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

        private byte[] GetDDPMessage(DDPMsgType ddpType, Dictionary<string, string>? data = null)
        {
            var request = $"{ddpType} * HTTP/1.1\n";
            if (data != null)
                foreach (var k in data)
                    request = request + $"{k.Key}:{k.Value}\n";
            request = request + $"device-discovery-protocol-version:{DISCOVERY_PROTOCOL_VERSION}\n";
            return Encoding.UTF8.GetBytes(request);
        }

        private byte[] CreateDiscoveryRequest() => GetDDPMessage(DDPMsgType.SRCH);

        private byte[] CreateWakeUpRequest(string credential)
        {
            var data = new Dictionary<string, string>
            {
                { "user-credential", credential},
                { "client-type", "vr" },
                { "auth-type","R" },
                { "model", "w" },
                { "app-type", "r" },
            };
            return GetDDPMessage(DDPMsgType.WAKEUP, data);
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
                var response = Encoding.ASCII.GetString(buffer);
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                string name = "Unknown";
                string? hostId = null;
                string? hostType = null;
                string? systemVersion = null;
                string? status = null;
                string? deviceDiscoveryProtocolVersion = null;
                var reStatus = new Regex(@"HTTP/1\.1\s+(?<code>\d+)\s+(?<status>.+)", RegexOptions.Compiled);
                foreach (var line in lines)
                {
                    var match = reStatus.Match(line);
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
                        status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析设备响应时发生错误");
                return null;
            }
        }

        private async Task<byte[]?> SendUdpRequestAsync(
            byte[] requestData,
            string targetIp,
            int targetPort,
            int timeoutMs = 2000,
            int localport = CLIENT_PORT,
            bool receiveData = false,
            CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(targetIp, out var targetAddress))
                throw new ArgumentException($"无效的IP地址: {targetIp}");

            var localEndPoint = new IPEndPoint(IPAddress.Any, localport);
            using var client = new UdpClient(localEndPoint);
            client.EnableBroadcast = true;

            var targetEndPoint = new IPEndPoint(targetAddress, targetPort);

            await client.SendAsync(requestData, requestData.Length, targetEndPoint);

            var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            if (receiveData)
                while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var receiveTask = client.ReceiveAsync();
                        var delayTask = Task.Delay(Math.Max(0, (int)(endTime - DateTime.UtcNow).TotalMilliseconds), cancellationToken);
                        var completed = await Task.WhenAny(receiveTask, delayTask);

                        if (completed == receiveTask)
                            return receiveTask.Result.Buffer;
                        else
                            break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }

            return null;
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
