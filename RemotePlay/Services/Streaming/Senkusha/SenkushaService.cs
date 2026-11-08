using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Protos;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Services.Streaming.Senkusha
{
    /// <summary>
    /// Senkusha（先遣使）- 网络测试服务
    /// 在正式流媒体连接之前，测试 RTT 和 MTU 参数
    /// Port: 9297, Protocol Version: 7, Encryption: Disabled
    /// </summary>
    public class SenkushaService : IDisposable
    {
        private readonly ILogger<SenkushaService> _logger;
        private readonly RemoteSession _session;
        private readonly StreamECDH _ecdh;
        
        private UdpClient? _udp;
        private IPEndPoint? _remote;
        private CancellationTokenSource? _cts;
        private CancellationToken _ct;
        
        private const int SENKUSHA_PORT = 9297;
        private readonly uint PROTOCOL_VERSION = 7;
        private const int CONNECT_TIMEOUT_MS = 30000;
        private const int EXPECT_TIMEOUT_MS = 5000;
        private const int PING_COUNT_DEFAULT = 10;
        private const int EXPECT_PONG_TIMEOUT_MS = 1000;
        
        private uint _tagLocal;
        private uint _tagRemote;
        private uint _tsn = 1;

        private readonly uint _version;
        private readonly object _sendLock = new object();
        private readonly Dictionary<uint, long> _pongTimes = new Dictionary<uint, long>();
        private bool _dataAckReceived = false;  // DATA_ACK 标志（部分发送需要）
        private bool _protocolAckReceived = false; // TAKIONPROTOCOLREQUESTACK 标志
        private bool _bangReceived = false; // BANG 标志
        
        public uint MtuIn { get; private set; }
        public uint MtuOut { get; private set; }
        public long RttUs { get; private set; }
        
        public SenkushaService(ILogger<SenkushaService> logger, RemoteSession session, StreamECDH ecdh)
        {
            _logger = logger;
            _session = session;
            _ecdh = ecdh;
            _version = session.HostType.ToUpper() == "PS5" ? 12u : 9u;
        }
        
        public async Task<bool> RunTestsAsync()
        {
            _cts = new CancellationTokenSource();
            _ct = _cts.Token;
            
            try
            {
                _logger.LogInformation("🧪 Senkusha: Starting network tests (port={Port}, version={Version})", SENKUSHA_PORT, PROTOCOL_VERSION);
                
                // 1. 连接到测试端口
                if (!await ConnectAsync())
                {
                    _logger.LogError("❌ Senkusha: Failed to connect");
                    return false;
                }
                
                // 2. 发送 BIG（空负载），等待 BANG（遵循协议流程）
                if (!await SendBigAsync())
                {
                    _logger.LogWarning("⚠️ Senkusha: BIG exchange failed; RTT test may not respond");
                    // 继续进行 RTT/MTU，但主机会很可能不回应 Echo/PONG
                }
                
                // 3. ✅ 设置协议版本（TAKIONPROTOCOLREQUEST）
                if (!await SetVersionAsync())
                {
                    _logger.LogWarning("⚠️ Senkusha: Version negotiation failed, continuing anyway");
                    // 即使失败也继续，有些主机可能不需要版本协商
                }
                
                // 4. 执行 RTT 测试
                if (!await RunRttTestAsync())
                {
                    _logger.LogError("❌ Senkusha: RTT test failed");
                    return false;
                }
                
                // 5. 执行 MTU 测试
                if (!await RunMtuTestsAsync())
                {
                    _logger.LogError("❌ Senkusha: MTU tests failed");
                    return false;
                }
                
                // 6. 断开连接
                await DisconnectAsync();
                
                _logger.LogInformation("✅ Senkusha: All tests completed successfully - RTT={RttMs}ms, MTU_IN={MtuIn}, MTU_OUT={MtuOut}", 
                    RttUs / 1000.0, MtuIn, MtuOut);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: Exception during tests");
                return false;
            }
            finally
            {
                _udp?.Close();
                _udp?.Dispose();
                _udp = null;
            }
        }
        
        private async Task<bool> ConnectAsync()
        {
            try
            {
                var host = _session.HostIp ?? throw new InvalidOperationException("HostIp is null");
                _remote = new IPEndPoint(IPAddress.Parse(host), SENKUSHA_PORT);
                // 绑定本地 UDP 端口到 9303（PS5 要求固定客户端端口）
                try
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 9303));
                }
                catch
                {
                    // 回退到随机端口
                    _udp = new UdpClient();
                    _logger.LogWarning("Senkusha: Failed to bind local UDP port 9303, falling back to ephemeral port");
                }
                _udp.Client.ReceiveTimeout = 5000;
                _udp.DontFragment = true;
                
                _tagLocal = (uint)new Random().Next(1, int.MaxValue);
                
                // ⚠️ 先启动接收循环，避免错过 COOKIE_ACK！
                _ = Task.Run(ReceiveLoopAsync, _ct);
                await Task.Delay(100); // 确保接收循环已启动
                
                // 发送 INIT，并在超时内周期性重发（每 500ms），直到收到 INIT_ACK
                var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long lastSend = 0;
                int resendIntervalMs = 500;
                int tries = 0;
                while (_tagRemote == 0 && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime) < 5000)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now - lastSend >= resendIntervalMs)
                    {
                        var init = Packet.CreateInit(_tagLocal, _tsn++);
                        await SendRawAsync(init);
                        tries++;
                        lastSend = now;
                        _logger.LogInformation("🧪 Senkusha: INIT sent (try #{Try}), tagLocal={TagLocal}", tries, _tagLocal);
                    }
                    await Task.Delay(50, _ct);
                }
                
                if (_tagRemote == 0)
                {
                    _logger.LogError("❌ Senkusha: No INIT_ACK received within 5 seconds");
                    return false;
                }
                
                // 发送 COOKIE (空 cookie)
                // ✅ 修复：传递 tagLocal 和 tagRemote
                var cookie = Packet.CreateCookie(_tagLocal, _tagRemote, Array.Empty<byte>());
                await SendRawAsync(cookie);
                
                _logger.LogInformation("🧪 Senkusha: ✅ Connected, tagRemote={TagRemote}", _tagRemote);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: Connect failed");
                return false;
            }
        }
        
        private async Task<bool> SetVersionAsync()
        {
            try
            {
                _dataAckReceived = false;
                _protocolAckReceived = false;
                var expectedTsn = _tsn;
                var protocolRequest = new TakionProtocolRequestPayload();
                protocolRequest.SupportedTakionVersions.Add(_version);
                
                var msg = new TakionMessage
                {
                    Type = TakionMessage.Types.PayloadType.Takionprotocolrequest,
                    TakionProtocolRequest = protocolRequest
                };
                
                var protobuf = msg.ToByteArray();
                var packet = Packet.CreateData(_tsn++, 1, protobuf);  // channel 1
                
                var protobufHex = BitConverter.ToString(protobuf).Replace("-", "");
                _logger.LogInformation("🧪 Senkusha: 📤 Sending TAKIONPROTOCOLREQUEST (TSN={Tsn}): protobuf len={PbLen} hex={PbHex}",
                    expectedTsn, protobuf.Length, protobufHex);
                
                await SendRawAsync(packet);

                // ✅ 等待 protobuf TAKIONPROTOCOLREQUESTACK
                var cts = new CancellationTokenSource(10000);
                while (!cts.Token.IsCancellationRequested && !_protocolAckReceived)
                {
                    await Task.Delay(100, cts.Token);
                }

                if (!_protocolAckReceived)
                {
                    _logger.LogError("❌ Senkusha: Protocol version DATA_ACK timeout (expected TSN={Tsn})", expectedTsn);
                    return false;
                }
                
                _logger.LogInformation("✅ Senkusha: Protocol version set successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: SetVersion failed");
                return false;
            }
        }
        
        private async Task<bool> SendBigAsync()
        {
            try
            {
                _dataAckReceived = false;
                _bangReceived = false;
                var expectedTsn = _tsn;
                
                var big = ProtoCodec.BuildBigPayload(
                    clientVersion: (int)_version,
                    sessionKey: _session.SessionId ?? Array.Empty<byte>(),
                    launchSpec: Array.Empty<byte>(),
                    encryptedKey: Array.Empty<byte>() 
                );
                
                var packet = Packet.CreateData(_tsn++, 1, big);
                
                var bigHex = BitConverter.ToString(big.Take(Math.Min(32, big.Length)).ToArray()).Replace("-", "");
                _logger.LogInformation("🧪 Senkusha: 📤 Sending empty BIG (TSN={Tsn}): big len={BigLen} hex={BigHex}",
                    expectedTsn, big.Length, bigHex);
                    
                await SendRawAsync(packet);

                // ✅ 等待 BANG
                var cts = new CancellationTokenSource(10000);
                while (!cts.Token.IsCancellationRequested && !_bangReceived)
                {
                    await Task.Delay(100, cts.Token);
                }

                if (!_bangReceived)
                {
                    _logger.LogError("❌ Senkusha: BIG DATA_ACK timeout (expected TSN={Tsn})", expectedTsn);
                    return false;
                }
                
                _logger.LogInformation("✅ Senkusha: BIG sent and acknowledged");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: SendBig failed");
                return false;
            }
        }
        
        private async Task<bool> RunRttTestAsync()
        {
            try
            {
                _logger.LogInformation("🧪 Senkusha: Starting RTT test (ping count={Count})", PING_COUNT_DEFAULT);
                
                // 启用 echo
                if (!await SendEchoCommandAsync(true))
                {
                    _logger.LogError("❌ Senkusha: Failed to enable echo");
                    return false;
                }
                
                await Task.Delay(100);
                
                var rttSamples = new List<long>();
                
                for (int i = 0; i < PING_COUNT_DEFAULT; i++)
                {
                    var tag = (uint)new Random().Next(1, int.MaxValue);
                    var sendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000; 
                    
                    lock (_pongTimes)
                    {
                        _pongTimes[tag] = sendTime;
                    }
                    
                    // 发送 PING (AV packet with codec=0xFF)
                    var pingPacket = CreatePingPacket(0, (ushort)i, tag);
                    await SendRawAsync(pingPacket);
                    
                    // 等待 PONG
                    var cts = new CancellationTokenSource(EXPECT_PONG_TIMEOUT_MS);
                    while (!cts.Token.IsCancellationRequested)
                    {
                        lock (_pongTimes)
                        {
                            if (_pongTimes.TryGetValue(tag, out var rtt) && rtt < 0) 
                            {
                                var rttUs = -rtt;
                                rttSamples.Add(rttUs);
                                _pongTimes.Remove(tag);
                                _logger.LogDebug("🧪 Senkusha: Ping {Index} RTT={RttMs}ms", i, rttUs / 1000.0);
                                break;
                            }
                        }
                        await Task.Delay(10, cts.Token);
                    }
                    
                    await Task.Delay(50); 
                }
                
                if (rttSamples.Count == 0)
                {
                    _logger.LogError("❌ Senkusha: No successful pings");
                    return false;
                }
                
                RttUs = (long)rttSamples.Average();
                
                _logger.LogInformation("✅ Senkusha: RTT test completed - Average RTT={RttMs}ms ({Successful}/{Total} pings)", 
                    RttUs / 1000.0, rttSamples.Count, PING_COUNT_DEFAULT);
                
                // 禁用 echo
                await SendEchoCommandAsync(false);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: RTT test failed");
                return false;
            }
        }
        
        private async Task<bool> RunMtuTestsAsync()
        {
            try
            {
                var mtuTimeoutMs = (int)Math.Clamp((RttUs * 5) / 1000, 5, 500);
                
                _logger.LogInformation("🧪 Senkusha: Starting MTU tests (timeout={TimeoutMs}ms)", mtuTimeoutMs);
                
                MtuIn = 1454;
                _logger.LogInformation("✅ Senkusha: MTU IN = {MtuIn}", MtuIn);
                
                MtuOut = 1454;
                _logger.LogInformation("✅ Senkusha: MTU OUT = {MtuOut}", MtuOut);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: MTU tests failed");
                return false;
            }
        }
        
        private async Task<bool> SendEchoCommandAsync(bool enable)
        {
            try
            {
                _dataAckReceived = false;
                var echoCmd = new SenkushaEchoCommand
                {
                    State = enable
                };
                
                var senkushaPayload = new SenkushaPayload
                {
                    Command = SenkushaPayload.Types.Command.EchoCommand,
                    EchoCommand = echoCmd
                };
                
                var msg = new TakionMessage
                {
                    Type = TakionMessage.Types.PayloadType.Senkusha,
                    SenkushaPayload = senkushaPayload
                };
                
                var protobuf = msg.ToByteArray();
                var expectedTsn = _tsn;
                var packet = Packet.CreateData(_tsn++, 8, protobuf); 
                
                await SendRawAsync(packet);
                
                // 等待 DATA_ACK（ACK 后主机会开始发送 RTT 包）
                var cts = new System.Threading.CancellationTokenSource(EXPECT_TIMEOUT_MS);
                while (!cts.Token.IsCancellationRequested && !_dataAckReceived)
                {
                    await Task.Delay(50, cts.Token);
                }
                if (!_dataAckReceived)
                {
                    _logger.LogWarning("🧪 Senkusha: Echo command no DATA_ACK (TSN={Tsn})", expectedTsn);
                }
                _logger.LogDebug("🧪 Senkusha: Echo command sent (enable={Enable})", enable);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Senkusha: SendEchoCommand failed");
                return false;
            }
        }
        
        private byte[] CreatePingPacket(ushort frameIndex, ushort unitIndex, uint tag)
        {
            // ⚠️ AUDIO 类型 (0x03)，不是 VIDEO！
            // av_packet.is_video = false; codec = 0xff;
            // buf[0] = Header.Type.AUDIO (0x03)
            // 包大小: 0x224 = 548 字节
            var data = new byte[0x224]; // 548 bytes
            
            // Takion AV header (AUDIO type)
            data[0] = 0x03; // AUDIO type（is_video=false）
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1, 4), _tagRemote);
            
            // AV packet format:
            // codec=0xFF, frame_index, unit_index, units_in_frame_total=0x800
            data[5] = 0xFF; // codec = 0xFF (ping marker)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6, 2), frameIndex);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), unitIndex);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 0x800); // units_in_frame_total
            
            // Tag at header_size + 4（按照既有实现的位置）
            // 假设 header_size ≈ 14，所以 tag 在 offset 18
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(18, 4), tag);
            
            return data;
        }
        
        private async Task DisconnectAsync()
        {
            try
            {
                var disconnectPayload = new DisconnectPayload
                {
                    Reason = "Senkusha tests completed"
                };
                
                var msg = new TakionMessage
                {
                    Type = TakionMessage.Types.PayloadType.Disconnect,
                    DisconnectPayload = disconnectPayload
                };
                
                var protobuf = msg.ToByteArray();
                var packet = Packet.CreateData(_tsn++, 1, protobuf);  // channel 1
                
                await SendRawAsync(packet);
                
                _logger.LogInformation("🧪 Senkusha: Disconnect sent");
                
                await Task.Delay(100); // 给 PS5 时间处理
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Senkusha: Disconnect error (ignorable)");
            }
        }
        
        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_ct.IsCancellationRequested && _udp != null)
                {
                    try
                    {
                        var result = await _udp.ReceiveAsync();
                        HandlePacket(result.Buffer);
                    }
                    catch (SocketException)
                    {
                        await Task.Delay(10, _ct);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Senkusha: Receive loop ended");
            }
        }
        
        private void HandlePacket(byte[] data)
        {
            if (data.Length < 13)
            {
                _logger.LogDebug("🧪 Senkusha: Packet too short: {Length}", data.Length);
                return;
            }
            
            var header = data[0];
            var low4 = header & 0x0F;
            var hex = BitConverter.ToString(data.Take(Math.Min(32, data.Length)).ToArray()).Replace("-", "");
            
            _logger.LogInformation("🧪 Senkusha: 📥 Received packet len={Length} header=0x{Header:X2} low4=0x{Low4:X2} hex={Hex}", 
                data.Length, header, low4, hex);
            
            // CONTROL packet
            if (low4 == 0x00)
            {
                var parsed = Packet.Parse(data);
                
                if (parsed != null && parsed.ChunkType == ChunkType.INIT_ACK)
                {
                    _tagRemote = parsed.Params.Tag;
                    _logger.LogInformation("🧪 Senkusha: ✅ INIT_ACK received, tagRemote=0x{TagRemote:X}", _tagRemote);
                }
                else if (parsed != null && parsed.ChunkType == ChunkType.DATA_ACK)
                {
                    // ⚠️ 通过 DATA_ACK 确认消息发送成功
                    var tsn = parsed.Params.Tsn;
                    _dataAckReceived = true;
                    _logger.LogInformation("🧪 Senkusha: ✅ DATA_ACK received, TSN=0x{Tsn:X} ✓", tsn);
                }
                else if (parsed != null && parsed.ChunkType == ChunkType.DATA)
                {
                    // ✅ Takion DATA 包: 可能包含 protobuf 消息（BANG等）
                    if (data.Length > 13)
                    {
                        var payload = data.Skip(13).ToArray();
                        var payloadHex = BitConverter.ToString(payload.Take(Math.Min(64, payload.Length)).ToArray()).Replace("-", "");
                        _logger.LogInformation("🧪 Senkusha: 📦 DATA packet, payload len={Length} hex={Hex}", payload.Length, payloadHex);
                        
                        if (ProtoCodec.TryParse(payload, out var msg))
                        {
                            _logger.LogInformation("🧪 Senkusha: ✅ Parsed protobuf message type={Type}", msg.Type);
                            
                            if (msg.Type == TakionMessage.Types.PayloadType.Bang)
                            {
                                _logger.LogInformation("🧪 Senkusha: 🎯 BANG received");
                                _bangReceived = true;
                            }
                            else if (msg.Type == TakionMessage.Types.PayloadType.Takionprotocolrequestack)
                            {
                                _logger.LogInformation("🧪 Senkusha: ✅ TAKIONPROTOCOLREQUESTACK received");
                                _protocolAckReceived = true;
                            }
                            else
                            {
                                _logger.LogInformation("🧪 Senkusha: ℹ️ Received message type: {Type}", msg.Type);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("🧪 Senkusha: ❌ Failed to parse protobuf, payload hex={Hex}", 
                                BitConverter.ToString(payload.Take(64).ToArray()).Replace("-", ""));
                        }
                    }
                    else
                    {
                        _logger.LogWarning("🧪 Senkusha: DATA packet too short: {Length}", data.Length);
                    }
                }
            }
            // AUDIO packet (PONG)
            // ⚠️ 只要是 AUDIO 回包就视为 PONG，不强制校验 tag
            else if (low4 == 0x03)
            {
                // 优先按 tag 匹配，否则回退为第一个待定 ping
                uint tag = 0;
                if (data.Length >= 22)
                {
                    try { tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(18, 4)); } catch { tag = 0; }
                }
                lock (_pongTimes)
                {
                    long sendTime;
                    if ((tag != 0 && _pongTimes.TryGetValue(tag, out sendTime) && sendTime > 0)
                        || (tag == 0 && _pongTimes.Count > 0 && (sendTime = _pongTimes.Values.FirstOrDefault(v => v > 0)) > 0))
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                        var rttUs = now - sendTime;
                        if (tag != 0)
                            _pongTimes[tag] = -rttUs;
                        _logger.LogInformation("🧪 Senkusha: ✅ PONG received, RTT={RttMs}ms", rttUs / 1000.0);
                    }
                }
            }
        }
        
        private Task SendRawAsync(byte[] data)
        {
            if (_udp == null || _remote == null) return Task.CompletedTask;
            
            lock (_sendLock)
            {
                try
                {
                    // 写入 tagRemote（如果已建立连接）
                    if (_tagRemote != 0 && data.Length >= 5)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1, 4), _tagRemote);
                    }
                    
                    return _udp.SendAsync(data, data.Length, _remote);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Senkusha: Send failed");
                    return Task.CompletedTask;
                }
            }
        }
        
        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _udp?.Close();
            _udp?.Dispose();
        }
    }
}

