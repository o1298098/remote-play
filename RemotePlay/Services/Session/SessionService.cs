using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.PlayStation;
using RemotePlay.Utils;
using RemotePlay.Utils.Crypto;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Launch;
using RemotePlay.Services.Streaming.Senkusha;

namespace RemotePlay.Services.Session
{
    public class SessionService : ISessionService
    {
        private readonly ILogger<SessionService> _logger;
        private readonly IDeviceDiscoveryService _discoveryService;
        private readonly ConcurrentDictionary<Guid, (RemoteSession Session, TcpClient Control, SessionCipher Cipher)> _sessions = new();
        private readonly ConcurrentDictionary<Guid, bool> _autoStartStreamFlags = new();
        private readonly ConcurrentDictionary<Guid, bool> _autoConnectControllerFlags = new();
        private readonly SessionConfig _sessionConfig;
        private readonly IServiceProvider _serviceProvider;

        private const int RP_PORT = 9295;
        private const string TYPE_PS4 = "PS4";
        private const string TYPE_PS5 = "PS5";
        private const string USER_AGENT = "remoteplay Windows";
        private static readonly byte[] DID_PREFIX = new byte[] { 0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80 };
        private static readonly byte[] HEARTBEAT_RESPONSE = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0xFE, 0x00, 0x00 };
        private const string OS_TYPE = "Win10.0.0";

        public SessionService(
            ILogger<SessionService> logger,
            IDeviceDiscoveryService discoveryService,
            IOptions<SessionConfig> sessionOptions,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _discoveryService = discoveryService;
            _sessionConfig = sessionOptions.Value;
            _serviceProvider = serviceProvider;
        }

        public async Task<RemoteSession> StartSessionAsync(
            string hostIp,
            DeviceCredentials credentials,
            string hostType,
            CancellationToken cancellationToken = default)
        {
            return await StartSessionAsync(hostIp, credentials, hostType, new SessionStartOptions(), cancellationToken);
        }

        public async Task<RemoteSession> StartSessionAsync(
            string hostIp,
            DeviceCredentials credentials,
            string hostType,
            SessionStartOptions options,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(hostIp)) throw new ArgumentException("hostIp 不能为空", nameof(hostIp));
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            hostType = string.IsNullOrWhiteSpace(hostType) ? "PS4" : hostType.ToUpper();

            _logger.LogInformation("Starting session, Host: {HostIp}, HostType: {HostType}", hostIp, hostType);

            // 1) 发现设备以校验连通性
            var device = await _discoveryService.DiscoverDeviceAsync(hostIp, _sessionConfig.ConnectTimeoutMs, cancellationToken);
            if (device == null)
                throw new InvalidOperationException($"无法连接到主机 {hostIp}");

            // 2) INIT: 发送初始化请求，获取服务器返回的 RP-Nonce
            var typeSlug = hostType == TYPE_PS5 ? "ps5" : "ps4";
            var initPath = $"/sie/{typeSlug}/rp/sess/init";

            var initRequest = BuildInitRequest(initPath, hostIp, hostType, credentials);
            var initResponse = await SendHttpRequestRawAsync(hostIp, RP_PORT, initRequest, keepAlive: false, cancellationToken);
            var rpNonceB64 = GetHeaderValue(initResponse.Headers, "RP-Nonce");
            _logger.LogInformation("RP-Nonce: {RPNonce}", rpNonceB64);
            _logger.LogInformation("INIT Response: {InitResponse}", initResponse.Headers);
            if (string.IsNullOrEmpty(rpNonceB64))
                throw new InvalidOperationException("当前主机已被远程连接占用");
            var rpNonce = Convert.FromBase64String(rpNonceB64);
            //var rpNonce = Convert.FromBase64String("T81oBINui9VnsCe3kNwDZA==");
            // 3) 会话密钥派生：根据 HOST 会话密钥与 RP-Key 计算 AES Key 与 rp_iv（rp_nonce）
            var (aesKey, rpIv) = DeriveSessionKeys(hostType, rpNonce, credentials.ServerKey);
            var cipher = new SessionCipher(hostType, aesKey, rpIv, 0);

            // 4) SESSION: 构造认证头，发起 ctrl 连接，并保留底层 TCP 以进行后续消息交互
            var ctrlPath = $"/sie/{typeSlug}/rp/sess/ctrl";
            var ctrlRequest = BuildSessionRequest(ctrlPath, hostIp, hostType, cipher, credentials, options);
            var keepAlive = await ConnectHttpKeepAliveAsync(hostIp, RP_PORT, ctrlRequest, cancellationToken);

            var headerText = keepAlive.HeaderText;
            var serverTypeHeader = ParseHeader(headerText, "RP-Server-Type");
            _logger.LogInformation(headerText);
            if (!string.IsNullOrEmpty(serverTypeHeader))
            {
                var stBytes = Convert.FromBase64String(serverTypeHeader);
                var stDecrypted = cipher.Decrypt(stBytes);
                var serverType = BitConverter.ToUInt16(stDecrypted, 0); // little-endian ushort
                _logger.LogInformation("Server Type: {ServerType}", serverType);
            }

            // 5) 存储会话
            var session = new RemoteSession
            {
                HostIp = hostIp,
                HostType = hostType,
                HostId = credentials.HostId,
                HostName = credentials.HostName,
                HandshakeKey = Array.Empty<byte>(),
                Secret = aesKey,
                SessionIv = rpIv,
                EncCounter = 0,
                DecCounter = 0,
                VideoKeyPos = 0,
                InputKeyPos = 0,
                Resolution = options.Resolution ?? _sessionConfig.DefaultResolution,
                Fps = options.Fps ?? _sessionConfig.DefaultFps,
                Quality = options.Quality ?? _sessionConfig.DefaultQuality,
                Bitrate = options.Bitrate,
                StreamType = options.StreamType
            };
            session.LaunchOptions = StreamLaunchOptionsResolver.Resolve(session);

            _sessions[session.Id] = (session, keepAlive.Client, cipher);
            _logger.LogInformation("Session established: {SessionId}", session.Id);

            // 6) 启动读取循环与心跳处理
            _autoStartStreamFlags[session.Id] = options.AutoStartStream;
            _autoConnectControllerFlags[session.Id] = options.AutoConnectController;
            _ = Task.Run(() => ReceiveLoopAsync(session.Id, keepAlive.Client, cipher, CancellationToken.None));
            return session;
        }

        public async Task<bool> StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (_sessions.TryRemove(sessionId, out var entry))
            {
                try
                {
                    // 自动断开控制器
                    try
                    {
                        var controllerService = _serviceProvider.GetRequiredService<IControllerService>();
                        await controllerService.DisconnectAsync(sessionId, cancellationToken);
                        _logger.LogInformation("✅ Controller auto-disconnected for session {SessionId}", sessionId);
                    }
                    catch (Exception exController)
                    {
                        _logger.LogDebug(exController, "Exception while disconnecting controller (may not have been connected)");
                    }
                    
                    entry.Session.StoppedAtUtc = DateTime.UtcNow;
                    entry.Control.Close();
                    entry.Control.Dispose();
                    
                    // 清理标志
                    _autoStartStreamFlags.TryRemove(sessionId, out _);
                    _autoConnectControllerFlags.TryRemove(sessionId, out _);
                    
                    _logger.LogInformation("Session closed: {SessionId}", sessionId);
                }
                catch { }
                return true;
            }
            return false;
        }

        public async Task<bool> SendInputAsync(Guid sessionId, InputState input, CancellationToken cancellationToken = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
                return false;

            var control = entry.Control;
            if (!control.Connected) return false;

            var data = input.ToBytes();
            var msg = BuildMessage(0x20 /* 控制消息占位 */, data, entry.Cipher);
            var stream = control.GetStream();
            await stream.WriteAsync(msg, 0, msg.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }

        public Task<RemoteSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (_sessions.TryGetValue(sessionId, out var entry))
                return Task.FromResult<RemoteSession?>(entry.Session);
            return Task.FromResult<RemoteSession?>(null);
        }

        public Task<IReadOnlyList<RemoteSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            var list = _sessions.Values.Select(v => v.Session).ToList().AsReadOnly();
            return Task.FromResult<IReadOnlyList<RemoteSession>>(list);
        }

        public async Task<bool> WaitReadyAsync(Guid sessionId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // 简化：轮询直到收到 SessionId 或超时
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < timeout)
            {
                if (_sessions.TryGetValue(sessionId, out var entry))
                {
                    if (entry.Session.SessionId?.Length > 0)
                        return true;
                }
                await Task.Delay(50, cancellationToken);
            }
            return false;
        }

        public async Task<bool> StandbyAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry))
                return false;
            var msg = BuildMessage(0x50, Array.Empty<byte>(), entry.Cipher); // STANDBY
            var stream = entry.Control.GetStream();
            await stream.WriteAsync(msg, 0, msg.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }

        private static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

        private string GetVersionByType(string hostType) => hostType == TYPE_PS5 ? "1.0" : "10.0";

        private byte[] BuildInitRequest(string path, string host, string hostType, DeviceCredentials credentials)
        {
            var registKeyHex = credentials.RegistrationKey?.Length > 0
                ? ToHex(credentials.RegistrationKey)
                : string.Empty;

            var sb = new StringBuilder();
            sb.Append($"GET {path} HTTP/1.1\r\n");
            sb.Append($"Host: {host}:{RP_PORT}\r\n");
            sb.Append($"User-Agent: {USER_AGENT}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Content-Length: 0\r\n");
            sb.Append($"RP-Registkey: {registKeyHex}\r\n");
            sb.Append($"RP-Version: {GetVersionByType(hostType)}\r\n\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private (byte[] aesKey, byte[] rpIv) DeriveSessionKeys(string hostType, byte[] nonce, byte[] rpKey)
        {
            // 会话密钥推导逻辑
            var rpIv = new byte[16];
            var aesKey = new byte[16];

            var sessionKey0 = hostType == TYPE_PS5 ? Key.SESSION_KEY_0_PS5 : Key.SESSION_KEY_0_PS4;
            var sessionKey1 = hostType == TYPE_PS5 ? Key.SESSION_KEY_1_PS5 : Key.SESSION_KEY_1_PS4;

            var key0 = sessionKey0.Skip((nonce[0] >> 3) * 112).ToArray();
            for (int i = 0; i < 16; i++)
            {
                int shift = hostType == TYPE_PS5 ? (nonce[i] - 45 - i) : (nonce[i] + 54 + i);
                shift ^= key0[i];
                rpIv[i] = (byte)(shift % 256);
            }

            var key1 = sessionKey1.Skip((nonce[7] >> 3) * 112).ToArray();
            for (int i = 0; i < 16; i++)
            {
                int shift;
                if (hostType == TYPE_PS5)
                {
                    shift = rpKey[i] + 24 + i;
                    shift ^= nonce[i];
                    shift ^= key1[i];
                }
                else
                {
                    shift = (key1[i] ^ rpKey[i]) + 33 + i;
                    shift ^= nonce[i];
                }
                aesKey[i] = (byte)(shift % 256);
            }

            return (aesKey, rpIv);
        }

        private static byte[] GenDid()
        {
            var rand = new byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rand);
            byte[] did = new byte[DID_PREFIX.Length + 16 + 6];
            Buffer.BlockCopy(DID_PREFIX, 0, did, 0, DID_PREFIX.Length);
            Buffer.BlockCopy(rand, 0, did, DID_PREFIX.Length, rand.Length);
            return did;
        }

        private byte[] BuildSessionRequest(string path, string host, string hostType, SessionCipher cipher, DeviceCredentials credentials, SessionStartOptions options)
        {
            var registKeyBytes = credentials.RegistrationKey;
            var rkPad = new byte[registKeyBytes.Length + 8];
            Buffer.BlockCopy(registKeyBytes, 0, rkPad, 0, registKeyBytes.Length);
            var authB64 = Convert.ToBase64String(cipher.Encrypt(rkPad));
            // regist_key bytes + 8 zeros, encrypted then base64
            var didB64 = Convert.ToBase64String(cipher.Encrypt(GenDid()));
            var osBytes = Encoding.ASCII.GetBytes(OS_TYPE);
            if (osBytes.Length < 10)
            {
                osBytes = osBytes.Concat(Enumerable.Repeat((byte)0x00, 10 - osBytes.Length)).ToArray();
            }

            var osB64 = Convert.ToBase64String(cipher.Encrypt(osBytes));

            var bitrateBytes = new byte[4];
            if (!string.IsNullOrWhiteSpace(options.Bitrate) && int.TryParse(options.Bitrate, out var bitrateValue) && bitrateValue > 0)
            {
                bitrateBytes[0] = (byte)(bitrateValue & 255);
                bitrateBytes[1] = (byte)((bitrateValue >> 8) & 255);
                bitrateBytes[2] = (byte)((bitrateValue >> 16) & 255);
                bitrateBytes[3] = (byte)((bitrateValue >> 24) & 255);
            }
            var bitrateB64 = Convert.ToBase64String(cipher.Encrypt(bitrateBytes));
            // PS5 需要提供 RP-StreamingType，按 Python 版实现为 4 字节小端整数
            // 这里先采用 H264 缺省值（0），后续可根据编解码/分辨率做映射
            string streamTypeB64 = string.Empty;
            if (hostType == TYPE_PS5)
            {
                int streamTypeCode = 1; // H264 缺省 (参考 Python 常量: H264=1)
                if (!string.IsNullOrWhiteSpace(options.StreamType) && int.TryParse(options.StreamType, out var parsedStreamType) && parsedStreamType > 0)
                {
                    streamTypeCode = parsedStreamType;
                }
                var stBytes = new byte[4]
                {
                    (byte)(streamTypeCode & 255),
                    (byte)((streamTypeCode >> 8) & 255),
                    (byte)((streamTypeCode >> 16) & 255),
                    (byte)((streamTypeCode >> 24) & 255)
                };
                streamTypeB64 = Convert.ToBase64String(cipher.Encrypt(stBytes));
            }

            var sb = new StringBuilder();
            sb.Append($"GET {path} HTTP/1.1\r\n");
            sb.Append($"Host: {host}:{RP_PORT}\r\n");
            sb.Append($"User-Agent: {USER_AGENT}\r\n");
            sb.Append("Connection: keep-alive\r\n");
            sb.Append("Content-Length: 0\r\n");
            sb.Append($"RP-Auth: {authB64}\r\n");
            sb.Append($"RP-Version: {GetVersionByType(hostType)}\r\n");
            sb.Append($"RP-Did: {didB64}\r\n");
            sb.Append("RP-ControllerType: 3\r\n");
            sb.Append("RP-ClientType: 11\r\n");
            sb.Append($"RP-OSType: {osB64}\r\n");
            sb.Append("RP-ConPath: 1\r\n");
            sb.Append($"RP-StartBitrate: {bitrateB64}\r\n");
            if (hostType == TYPE_PS5)
                sb.Append($"RP-StreamingType: {streamTypeB64}\r\n");
            sb.Append("\r\n");
            _logger.LogInformation(sb.ToString());
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private sealed class RawHttpResponse
        {
            public string Headers { get; set; } = string.Empty;
            public byte[] Body { get; set; } = Array.Empty<byte>();
        }

        private static string GetHeaderValue(string headersText, string name)
        {
            var lines = headersText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var l in lines)
            {
                var idx = l.IndexOf(':');
                if (idx > 0)
                {
                    var key = l.Substring(0, idx).Trim();
                    if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return l.Substring(idx + 1).Trim();
                    }
                }
            }
            return string.Empty;
        }

        private static string ParseHeader(string headersText, string name) => GetHeaderValue(headersText, name);

        private async Task<RawHttpResponse> SendHttpRequestRawAsync(string host, int port, byte[] requestBytes, bool keepAlive, CancellationToken ct)
        {
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(host, port, ct);
            using var stream = client.GetStream();
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, ct);
            await stream.FlushAsync(ct);

            var headerBuffer = new List<byte>();
            var buf = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int r = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (r <= 0) break;
                headerBuffer.AddRange(buf.AsSpan(0, r).ToArray());
                if (headerBuffer.Count >= 4)
                {
                    for (int i = 0; i <= headerBuffer.Count - 4; i++)
                    {
                        if (headerBuffer[i] == '\r' && headerBuffer[i + 1] == '\n' && headerBuffer[i + 2] == '\r' && headerBuffer[i + 3] == '\n')
                        {
                            headerEnd = i + 4;
                            break;
                        }
                    }
                }
            }

            var headers = Encoding.ASCII.GetString(headerBuffer.Take(headerEnd > 0 ? headerEnd : headerBuffer.Count).ToArray());
            var body = headerBuffer.Skip(headerEnd > 0 ? headerEnd : 0).ToArray();
            if (!keepAlive)
            {
                return new RawHttpResponse { Headers = headers, Body = body };
            }
            // keepAlive 下，由上层使用另一方法获取连接
            return new RawHttpResponse { Headers = headers, Body = body };
        }

        private sealed class KeepAliveConnection
        {
            public TcpClient Client { get; set; } = null!;
            public string HeaderText { get; set; } = string.Empty;
        }

        private async Task<KeepAliveConnection> ConnectHttpKeepAliveAsync(string host, int port, byte[] requestBytes, CancellationToken ct)
        {
            var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(host, port, ct);
            var stream = client.GetStream();
            await stream.WriteAsync(requestBytes, 0, requestBytes.Length, ct);
            await stream.FlushAsync(ct);

            var headerBuffer = new List<byte>();
            var buf = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int r = await stream.ReadAsync(buf, 0, buf.Length, ct);
                if (r <= 0) break;
                headerBuffer.AddRange(buf.AsSpan(0, r).ToArray());
                if (headerBuffer.Count >= 4)
                {
                    for (int i = 0; i <= headerBuffer.Count - 4; i++)
                    {
                        if (headerBuffer[i] == '\r' && headerBuffer[i + 1] == '\n' && headerBuffer[i + 2] == '\r' && headerBuffer[i + 3] == '\n')
                        {
                            headerEnd = i + 4;
                            break;
                        }
                    }
                }
            }
            var headers = Encoding.ASCII.GetString(headerBuffer.Take(headerEnd > 0 ? headerEnd : headerBuffer.Count).ToArray());
            // 剩余部分属于第一帧数据体，保留在接收循环中继续处理
            return new KeepAliveConnection { Client = client, HeaderText = headers };
        }

        private byte[] BuildMessage(int msgType, byte[] payload, SessionCipher? cipher)
        {
            var p = payload ?? Array.Empty<byte>();
            var enc = cipher != null && p.Length > 0 ? cipher.Encrypt(p) : p;
            var buf = new byte[8 + enc.Length];
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)enc.Length);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), (ushort)msgType);
            buf[6] = 0; buf[7] = 0;
            if (enc.Length > 0)
                Buffer.BlockCopy(enc, 0, buf, 8, enc.Length);
            return buf;
        }

        private async Task ReceiveLoopAsync(Guid sessionId, TcpClient control, SessionCipher cipher, CancellationToken ct)
        {
            var stream = control.GetStream();
            var header = new byte[8];
            try
            {
                while (!ct.IsCancellationRequested && control.Connected)
                {
                    // 读取头部
                    int read = await ReadExactAsync(stream, header, 0, 8, ct);
                    if (read <= 0) break;
                    var payloadLen = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                    var msgType = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));

                    byte[] payload = Array.Empty<byte>();
                    if (payloadLen > 0)
                    {
                        payload = new byte[payloadLen];
                        await ReadExactAsync(stream, payload, 0, (int)payloadLen, ct);
                        payload = cipher.Decrypt(payload);
                    }

                    // 处理心跳与会话ID
                    if (msgType == 0x00FE) // HEARTBEAT_REQUEST
                    {
                        var resp = BuildMessage(0x01FE, HEARTBEAT_RESPONSE, cipher);
                        await stream.WriteAsync(resp, 0, resp.Length, ct);
                        await stream.FlushAsync(ct);
                    }
                    else if (msgType == 0x0033) // SESSION_ID
                    {
                        // ✅ Chiaki: payload[0] 是长度字节，只跳过 1 个字节
                        // payload++; payload_size--;
                        var raw = payload.Length > 1 ? payload.Skip(1).ToArray() : Array.Empty<byte>();
                        
                        // 📝 记录原始 payload 用于调试
                        _logger.LogInformation("📨 Received SESSION_ID: payload[0]=0x{FirstByte:X2} rawLen={RawLen} hex={Hex}",
                            payload.Length > 0 ? payload[0] : 0, raw.Length,
                            BitConverter.ToString(raw.Take(Math.Min(32, raw.Length)).ToArray()).Replace("-", ""));
                        
                        byte[] normalized = raw;
                        bool needFallback = false;
                        
                        // ✅ Chiaki 验证规则
                        if (raw.Length < 2)
                        {
                            _logger.LogError("❌ SessionId too short: {Len} bytes", raw.Length);
                            needFallback = true;
                        }
                        else if (payload.Length > 0 && payload[0] != 0x4a)
                        {
                            _logger.LogWarning("⚠️ SessionId first byte is 0x{FirstByte:X2}, expected 0x4a", payload[0]);
                            // Chiaki 只是警告，不使用 fallback
                        }
                        
                        if (!needFallback && raw.Length < 24)
                        {
                            _logger.LogError("❌ SessionId too short: {Len} bytes (min 24)", raw.Length);
                            needFallback = true;
                        }
                        
                        // 验证字符（只能是字母和数字）
                        if (!needFallback)
                        {
                            foreach (var b in raw)
                            {
                                char c = (char)b;
                                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                                {
                                    _logger.LogError("❌ SessionId contains invalid character: 0x{CharByte:X2} ('{Char}')", b, c);
                                    needFallback = true;
                                    break;
                                }
                            }
                        }
                        
                        if (needFallback)
                        {
                            // ✅ 生成 fallback sessionId（Chiaki 格式 - 不填充时间戳）
                            var timeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var timeStr = timeSeconds.ToString(); // ✅ Chiaki: 不填充，直接使用原始时间戳
                            
                            var randomBytes = new byte[48];
                            System.Security.Cryptography.RandomNumberGenerator.Fill(randomBytes);
                            var randomB64 = Convert.ToBase64String(randomBytes);
                            
                            var fallbackId = timeStr + randomB64;
                            normalized = System.Text.Encoding.UTF8.GetBytes(fallbackId);
                            _logger.LogWarning("⚠️ Using fallback SessionId (len={Len}): {FallbackId}", normalized.Length, fallbackId);
                        }
                        else
                        {
                            try
                            {
                                var sessionIdStr = System.Text.Encoding.UTF8.GetString(raw);
                                _logger.LogInformation("✅ Valid SessionId received: {SessionId}", sessionIdStr);
                            }
                            catch (System.Text.DecoderFallbackException)
                            {
                                var sb = new System.Text.StringBuilder(raw.Length);
                                foreach (var b in raw)
                                    sb.Append((char)b);
                                normalized = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                                _logger.LogInformation("✅ SessionId (normalized): {SessionId}", sb.ToString());
                            }
                        }

                        if (_sessions.TryGetValue(sessionId, out var entry))
                        {
                            entry.Session.SessionId = normalized;
                            // 触发会话就绪信号
                            _ = entry.Session.SessionReady.TrySetResult(true);
                        }
                        if (_autoStartStreamFlags.TryGetValue(sessionId, out var autoStart) && autoStart)
                        {
                            try
                            {
                                // ✅ Senkusha 阶段：网络测试（RTT + MTU）
                                _logger.LogInformation("🧪 Starting Senkusha network tests before main stream...");
                                if (_sessions.TryGetValue(sessionId, out var sessionEntry))
                                {
                                    var senkushaLogger = _serviceProvider.GetRequiredService<ILogger<SenkushaService>>();
                                    var ecdh = new StreamECDH();
                                    bool isTest = false;
                                    var senkusha = new SenkushaService(senkushaLogger, sessionEntry.Session, ecdh);
                                    if (isTest)
                                    {
                                        var senkushaSuccess = await senkusha.RunTestsAsync();

                                        if (senkushaSuccess)
                                        {
                                            _logger.LogInformation("✅ Senkusha tests passed - RTT={RttMs}ms, MTU_IN={MtuIn}, MTU_OUT={MtuOut}",
                                                senkusha.RttUs / 1000.0, senkusha.MtuIn, senkusha.MtuOut);

                                            // 保存测试结果到 session
                                            sessionEntry.Session.RttUs = senkusha.RttUs;
                                            sessionEntry.Session.MtuIn = (int)senkusha.MtuIn;
                                            sessionEntry.Session.MtuOut = (int)senkusha.MtuOut;
                                        }
                                        else
                                        {
                                            _logger.LogWarning("⚠️ Senkusha tests failed, proceeding with default values");
                                            // 使用默认值继续
                                            sessionEntry.Session.RttUs = 10000;
                                            sessionEntry.Session.MtuIn = 1454;
                                            sessionEntry.Session.MtuOut = 1454;
                                        }

                                        senkusha.Dispose();
                                    }
                                }
                                
                                // 等待一小段时间让 PS5 准备好
                                await Task.Delay(500, ct);
                                
                                // 启动主流媒体连接
                                var streamingService = _serviceProvider.GetRequiredService<IStreamingService>();
                                await streamingService.StartStreamAsync(sessionId, true, ct);
                                
                                // 自动连接并启动控制器
                                if (_autoConnectControllerFlags.TryGetValue(sessionId, out var autoConnect) && autoConnect)
                                {
                                    try
                                    {
                                        var controllerService = _serviceProvider.GetRequiredService<IControllerService>();
                                        var connected = await controllerService.ConnectAsync(sessionId, ct);
                                        if (connected)
                                        {
                                            await controllerService.StartAsync(sessionId, ct);
                                            _logger.LogInformation("✅ Controller auto-connected and started for session {SessionId}", sessionId);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("⚠️ Controller auto-connect failed for session {SessionId}", sessionId);
                                        }
                                    }
                                    catch (Exception exController)
                                    {
                                        _logger.LogWarning(exController, "Failed to auto-connect controller");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to auto-start streaming test");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Session receive loop error");
            }
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int r = await stream.ReadAsync(buffer, offset + readTotal, count - readTotal, ct);
                if (r <= 0) return readTotal;
                readTotal += r;
            }
            return readTotal;
        }
    }
}


