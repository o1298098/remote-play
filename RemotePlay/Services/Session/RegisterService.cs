using RemotePlay.Contracts.Services;
using RemotePlay.Models.PlayStation;
using RemotePlay.Utils.Crypto;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemotePlay.Services.Session
{
    public class RegisterService : IRegisterService
    {
        private readonly ILogger<RegisterService> _logger;
        private readonly IDeviceDiscoveryService _deviceDiscoveryService;

        // PlayStation Remote Play 协议常量
        private const int RP_PORT = 9295;
        private const string CLIENT_TYPE = "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f";
        private const string USER_AGENT = "remoteplay Windows";
        private const int REG_KEY_SIZE = 16;
        private const int REG_DATA_SIZE = 480;

        // PS4 常量
        private const string REG_PATH_PS4 = "/sie/ps4/rp/sess/rgst";
        private const string REG_INIT_PS4 = "SRC2";
        private const string REG_START_PS4 = "RES2";
        private const string RP_VERSION_PS4 = "10.0";

        // PS5 常量
        private const string REG_PATH_PS5 = "/sie/ps5/rp/sess/rgst";
        private const string REG_INIT_PS5 = "SRC3";
        private const string REG_START_PS5 = "RES3";
        private const string RP_VERSION_PS5 = "1.0";

        // 注册密钥 (简化版，实际应该从keys.py获取)
        private static readonly byte[] REG_KEY_0_PS4 = Key.REG_KEY_0_PS4;
        private static readonly byte[] REG_KEY_1_PS4 = Key.REG_KEY_1_PS4;
        private static readonly byte[] REG_KEY_0_PS5 = Key.REG_KEY_0_PS5;
        private static readonly byte[] REG_KEY_1_PS5 = Key.REG_KEY_1_PS5;

        public RegisterService(ILogger<RegisterService> logger, IDeviceDiscoveryService deviceDiscoveryService)
        {
            _logger = logger;
            _deviceDiscoveryService = deviceDiscoveryService;
        }

        public async Task<RegisterResult> RegisterDeviceAsync(string hostIp, string accountId, string pin, CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting device registration - host: {HostIp}, account: {AccountId}", hostIp, accountId);

            try
            {
                // 验证输入参数
                if (string.IsNullOrEmpty(hostIp))
                    throw new ArgumentException("主机IP不能为空", nameof(hostIp));
                if (string.IsNullOrEmpty(accountId))
                    throw new ArgumentException("账户ID不能为空", nameof(accountId));
                if (string.IsNullOrEmpty(pin))
                    throw new ArgumentException("PIN不能为空", nameof(pin));

                // 先进行设备发现以确定主机类型
                string hostType = "PS4"; // 默认使用 PS4，与常见环境一致
                try
                {
                    var deviceInfo = await _deviceDiscoveryService.DiscoverDeviceAsync(hostIp, 2000, cancellationToken);
                    if (deviceInfo != null && !string.IsNullOrEmpty(deviceInfo.HostType))
                    {
                        hostType = deviceInfo.HostType.ToUpper();
                        _logger.LogInformation("Detected host type: {HostType}", hostType);
                    }
                    else
                    {
                        _logger.LogWarning("Could not determine host type, using default: {Default}", hostType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Device discovery failed, using default host type {Default}", hostType);
                }

                // 检查设备是否处于注册模式
                if (!await RegistInitAsync(hostIp, hostType, cancellationToken))
                {
                    _logger.LogError("Device is not in registration mode; enable Remote Play on the device");
                    return new RegisterResult
                    {
                        Success = false,
                        ErrorMessage = "设备未处于注册模式",
                        Duration = DateTime.UtcNow - startTime
                    };
                }

                // 生成密钥和载荷
                var (cipher, headers, payload) = GetRegistCipherHeadersPayload(hostType, hostIp, accountId, pin);

                // 发送注册请求
                var response = await GetRegisterInfoAsync(hostIp, headers, payload, cancellationToken);

                // 解析响应
                var info = ParseResponse(cipher, response);
                var result = CreateRegisterResult(info, hostIp, accountId);
                result.Duration = DateTime.UtcNow - startTime;

                if (result.Success)
                {
                    _logger.LogInformation("Device registered successfully - host: {HostName} ({HostId})", result.HostName, result.HostId);
                }
                else
                {
                    _logger.LogWarning("Device registration failed - host: {HostIp}, error: {ErrorMessage}", hostIp, result.ErrorMessage);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Device registration cancelled - host: {HostIp}", hostIp);
                return new RegisterResult
                {
                    Success = false,
                    ErrorMessage = "注册被取消",
                    Duration = DateTime.UtcNow - startTime
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device registration - host: {HostIp}", hostIp);
                return new RegisterResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = DateTime.UtcNow - startTime
                };
            }
        }

        private async Task<bool> RegistInitAsync(string host, string hostType, CancellationToken cancellationToken)
        {
            var initCommand = hostType == "PS4" ? Encoding.ASCII.GetBytes(REG_INIT_PS4) : Encoding.ASCII.GetBytes(REG_INIT_PS5);
            var expectedResponse = hostType == "PS4" ? REG_START_PS4 : REG_START_PS5;

            const int timeoutMs = 3000;
            using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

            try
            {
                var targetEndPoint = new IPEndPoint(IPAddress.Parse(host), RP_PORT);
                await udpClient.SendAsync(initCommand, initCommand.Length, targetEndPoint);
                _logger.LogDebug("Sent registration init command: {Command}", Encoding.ASCII.GetString(initCommand));

                var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var receiveTask = udpClient.ReceiveAsync();
                        var delayTask = Task.Delay(Math.Max(0, (int)(endTime - DateTime.UtcNow).TotalMilliseconds), cancellationToken);
                        var completed = await Task.WhenAny(receiveTask, delayTask);

                        if (completed == receiveTask)
                        {
                            var response = receiveTask.Result;
                            _logger.LogDebug("Received response, length: {Length} bytes", response.Buffer.Length);

                            if (response.Buffer.Length >= 4)
                            {
                                var responseStr = Encoding.ASCII.GetString(response.Buffer, 0, 4);
                                _logger.LogDebug("Response content: {Response}", responseStr);

                                if (responseStr == expectedResponse)
                                {
                                    _logger.LogInformation("Registration started");
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Timed out waiting for response");
                            break;
                        }
                    }
                    catch (SocketException ex)
                    {
                        _logger.LogDebug("Socket exception while receiving data: {Message}", ex.Message);
                        break;
                    }
                }

                _logger.LogError("Did not receive expected registration response, expected: {Expected}", expectedResponse);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration init failed");
                return false;
            }
        }

        public (SessionCipher, byte[], byte[]) GetRegistCipherHeadersPayload(string hostType, string hostIp, string psnId, string pin)
        {
            var nonce = GenerateRandomBytes(16);
            //var nonce = Convert.FromHexString("5a716872d7db001e70072190577c14a2");
            var key0 = GenKey0(hostType, int.Parse(pin));
            var key1 = GenKey1(hostType, nonce);
            var payload = GetRegistPayload(key1);

            _logger.LogInformation("Registration key generated - Nonce: {Nonce}, Key0: {Key0}, Key1: {Key1}",
                Convert.ToHexString(nonce), Convert.ToHexString(key0), Convert.ToHexString(key1).ToLower());

            // Validate and debug user_rpid format (Base64)
            _logger.LogInformation("=== AccountId validation ===");
            _logger.LogInformation("Input psnId: {PsnId}", psnId);

            try
            {
                var decodedBytes = Convert.FromBase64String(psnId);
                _logger.LogInformation("Decoded bytes: {Hex}", Convert.ToHexString(decodedBytes));
                _logger.LogInformation("Decoded length: {Length} (expected 8 bytes)", decodedBytes.Length);

                if (decodedBytes.Length != 8)
                {
                    _logger.LogError("❌ Invalid AccountId length! Expected 8 bytes, got {Length} bytes", decodedBytes.Length);
                }
                else
                {
                    _logger.LogInformation("✅ AccountId length is valid");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AccountId is not a valid Base64 string!");
            }

            _logger.LogInformation("=== Validation complete ===");

            // 使用SessionCipher加密PSN ID并追加到payload
            var cipher = new SessionCipher(hostType, key0, nonce, 0);

            // 按照Python实现，需要先构建格式化的字符串，然后加密
            // Client-Type: <CLIENT_TYPE>\r\nNp-AccountId: <psn_id>\r\n
            var payloadString = $"Client-Type: {CLIENT_TYPE}\r\nNp-AccountId: {psnId}\r\n";
            var psnIdBytes = Encoding.UTF8.GetBytes(payloadString);

            _logger.LogInformation("PSN ID before encryption - length: {PlainLen}, content: {PlainContent}",
                psnIdBytes.Length, Convert.ToHexString(psnIdBytes).ToLower());

            var encPayload = cipher.Encrypt(psnIdBytes);

            _logger.LogInformation("PSN ID after encryption - length: {EncLen}, content: {EncContent}",
                encPayload.Length, Convert.ToHexString(encPayload).ToLower());

            var fullPayload = new List<byte>(payload);
            fullPayload.AddRange(encPayload);
            payload = fullPayload.ToArray();

            _logger.LogInformation("Full payload - total length: {TotalLen}, content: {Payload}",
                payload.Length, Convert.ToHexString(payload).ToLower());

            var headers = GetRegistHeaders(hostType, payload.Length);

            return (cipher, headers, payload);
        }

        private async Task<byte[]> GetRegisterInfoAsync(string host, byte[] headers, byte[] payload, CancellationToken cancellationToken)
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, RP_PORT, cancellationToken);

            if (!tcpClient.Connected)
                throw new IOException($"连接到 {host}:{RP_PORT} 失败");

            tcpClient.NoDelay = true;
            using var stream = tcpClient.GetStream();

            // 合并 headers + payload
            var requestBytes = new byte[headers.Length + payload.Length];
            Buffer.BlockCopy(headers, 0, requestBytes, 0, headers.Length);
            Buffer.BlockCopy(payload, 0, requestBytes, headers.Length, payload.Length);

            _logger.LogDebug("Sending registration request - headers length: {HeadersLen}, payload length: {PayloadLen}, total length: {TotalLen}",
                headers.Length, payload.Length, requestBytes.Length);
            _logger.LogDebug("Request headers:\n{Headers}", Encoding.UTF8.GetString(headers));

            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            var responseBuffer = new List<byte>();
            var temp = new byte[4096];
            int headerEndIndex = -1;

            // 读取直到拿到完整的HTTP头（\r\n\r\n）
            while (headerEndIndex < 0)
            {
                int read = await stream.ReadAsync(temp, 0, temp.Length, cancellationToken);
                if (read == 0)
                    break;

                responseBuffer.AddRange(temp.AsSpan(0, read).ToArray());

                if (responseBuffer.Count >= 4)
                {
                    for (int i = 0; i <= responseBuffer.Count - 4; i++)
                    {
                        if (responseBuffer[i] == '\r' && responseBuffer[i + 1] == '\n' &&
                            responseBuffer[i + 2] == '\r' && responseBuffer[i + 3] == '\n')
                        {
                            headerEndIndex = i + 4;
                            break;
                        }
                    }
                }
            }

            var responseBytes = responseBuffer.ToArray();
            _logger.LogDebug("HTTP headers read, total bytes so far: {Len}", responseBytes.Length);

            int contentLength = 0;
            if (headerEndIndex > 0)
            {
                var headerText = Encoding.ASCII.GetString(responseBytes, 0, headerEndIndex);
                _logger.LogDebug("Response headers:\n{Headers}", headerText);

                foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        var part = line.Substring("Content-Length:".Length).Trim();
                        if (int.TryParse(part, out var cl))
                            contentLength = cl;
                    }
                }

                int bodyBytesRead = responseBytes.Length - headerEndIndex;
                while (contentLength == 0 || bodyBytesRead < contentLength)
                {
                    int read = await stream.ReadAsync(temp, 0, temp.Length, cancellationToken);
                    if (read == 0)
                        break;

                    responseBuffer.AddRange(temp.AsSpan(0, read).ToArray());
                    bodyBytesRead += read;
                }
            }

            var fullResponse = responseBuffer.ToArray();
            _logger.LogDebug("Received response, length: {Length} bytes", fullResponse.Length);

            return fullResponse;
        }

        private Dictionary<string, string> ParseResponse(SessionCipher cipher, byte[] response)
        {
            var info = new Dictionary<string, string>();

            int headerEndIndex = -1;
            for (int i = 0; i <= response.Length - 4; i++)
            {
                if (response[i] == '\r' && response[i + 1] == '\n' && response[i + 2] == '\r' && response[i + 3] == '\n')
                {
                    headerEndIndex = i + 4;
                    break;
                }
            }

            if (headerEndIndex > 0)
            {
                var firstLineEnd = Array.IndexOf(response, (byte)'\n');
                string statusLine = firstLineEnd > 0
                    ? Encoding.ASCII.GetString(response, 0, firstLineEnd).Trim()
                    : Encoding.ASCII.GetString(response).Trim();

                if (statusLine.Contains("200 OK"))
                {
                    _logger.LogInformation("Registration succeeded");

                    var cipherText = response.AsSpan(headerEndIndex).ToArray();
                    if (cipherText.Length > 0)
                    {
                        try
                        {
                            var decryptedData = cipher.Decrypt(cipherText);
                            var dataStr = Encoding.UTF8.GetString(decryptedData).TrimEnd('\0');

                            var dataLines = dataStr.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in dataLines)
                            {
                                var parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
                                if (parts.Length == 2)
                                    info[parts[0]] = parts[1];
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error decrypting response");
                        }
                    }
                }
                else
                {
                    _logger.LogError("Registration failed, status: {Status}", statusLine);
                    _logger.LogDebug("Response: {Raw}", Encoding.UTF8.GetString(response));
                }
            }
            else
            {
                _logger.LogError("Invalid response format (\\r\\n\\r\\n not found)");
            }
            _logger.LogInformation("Registration info: {Info}", string.Join(", ", info.Select(kv => $"{kv.Key}={kv.Value}")));
            return info;
        }

        private RegisterResult CreateRegisterResult(Dictionary<string, string> info, string hostIp, string accountId)
        {
            if (info.Count > 0)
            {
                return new RegisterResult
                {
                    Success = true,
                    HostId = info.GetValueOrDefault("host-id", ""),
                    HostName = info.GetValueOrDefault("host-name", "Unknown"),
                    SystemVersion = info.GetValueOrDefault("system-version", "Unknown"),
                    RegistData = info,
                };
            }

            return new RegisterResult
            {
                Success = false,
                ErrorMessage = "注册失败 - 请检查日志获取详细错误信息"
            };
        }

        private byte[] GenKey0(string hostType, int pin)
        {
            var regKey = hostType == "PS4" ? REG_KEY_0_PS4 : REG_KEY_0_PS5;
            var key = new byte[REG_KEY_SIZE];

            for (int i = 0; i < REG_KEY_SIZE; i++)
            {
                key[i] = regKey[i * 32 + 1];
            }

            // 将PIN编码到最后的4个字节
            /*for (int i = 12; i < 16; i++)
            {
                    key[i] ^= (byte)((pin >> (8 * (15 - i))) & 0xFF);
            }*/

            for (int i = 12, shift = 0; i < REG_KEY_SIZE; i++, shift++)
            {
                key[i] ^= (byte)((pin >> (24 - (shift * 8))) & 255);
            }


            return key;
        }

        private byte[] GenKey1(string hostType, byte[] nonce)
        {
            var regKey = hostType == "PS4" ? REG_KEY_1_PS4 : REG_KEY_1_PS5;
            var key = new byte[REG_KEY_SIZE];
            var offset = hostType == "PS5" ? -45 : 41;

            for (int i = 0; i < REG_KEY_SIZE; i++)
            {
                var shift = regKey[i * 32 + 8];
                key[i] = (byte)(((nonce[i] ^ shift) + offset + i) % 256);
            }

            return key;
        }

        private byte[] GetRegistPayload(byte[] key1)
        {
            var regData = new byte[REG_DATA_SIZE];
            Array.Fill(regData, (byte)'A');

            var payload = new List<byte>();
            // 0-199: regData[0:199]
            payload.AddRange(regData.Take(199));
            // key1[8:]
            payload.AddRange(key1.Skip(8));
            // 207-401: regData[207:401]
            payload.AddRange(regData.Skip(207).Take(194));
            // key1[0:8]
            payload.AddRange(key1.Take(8));
            // 409-end: regData[409:]
            payload.AddRange(regData.Skip(409));

            return payload.ToArray();
        }

        private byte[] GetRegistHeaders(string hostType, int payloadLength)
        {
            var path = hostType == "PS4" ? REG_PATH_PS4 : REG_PATH_PS5;
            var version = hostType == "PS4" ? RP_VERSION_PS4 : RP_VERSION_PS5;

            // 注意：按照Python原实现，HOST字段使用硬编码的 10.0.2.15
            // Python注释说明："Doesn't Matter" - 这个值不影响功能
            var headers = $"POST {path} HTTP/1.1\r\n HTTP/1.1\r\n" +
                         "HOST: 10.0.2.15\r\n" +
                         $"User-Agent: {USER_AGENT}\r\n" +
                         "Connection: close\r\n" +
                         $"Content-Length: {payloadLength}\r\n" +
                         $"RP-Version: {version}\r\n\r\n";
            _logger.LogInformation(headers);
            return Encoding.UTF8.GetBytes(headers);
        }


        private byte[] GenerateRandomBytes(int length)
        {
            if (length <= 0)
                throw new ArgumentException("长度必须大于0", nameof(length));

            var bytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return bytes;
        }

    }
}
