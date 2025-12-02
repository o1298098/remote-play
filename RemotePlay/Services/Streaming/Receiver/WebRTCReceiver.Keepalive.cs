using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver Keepalive 机制部分
    /// </summary>
    public sealed partial class WebRTCReceiver
    {
        // ✅ TURN keepalive（服务器端必须主动发送，因为 ICE-Lite 不会自动发送）
        private IPEndPoint? _turnRelay; // TURN relay candidate 地址（仅用于日志和 TCP TURN）
        private string? _turnProtocol; // TURN 协议类型：UDP 或 TCP
        // ⚠️ 注意：UDP TURN 不使用独立 socket，通过 DataChannel/音频/视频包发送 keepalive
        // ❌ 不要创建 _turnKeepaliveUdpSocket，会破坏 NAT 映射导致黑屏
        private TcpClient? _turnKeepaliveTcpSocket; // 仅用于 TCP TURN 的独立 TCP socket
        private NetworkStream? _turnKeepaliveTcpStream; // TCP socket 的流
        private CancellationTokenSource? _turnKeepaliveCts; // TURN keepalive 取消令牌
        private Task? _turnKeepaliveTask; // TURN keepalive 任务（仅用于 TCP TURN）
        private const int TURN_KEEPALIVE_INTERVAL_MS = 5000; // TURN keepalive 间隔：5秒
        private void StartKeepalive()
        {
            if (_keepaliveTask != null && !_keepaliveTask.IsCompleted)
            {
                return;
            }
            
            StopKeepalive();
            
            CheckKeepaliveDataChannel();
            
            _keepaliveCts = new CancellationTokenSource();
            _keepaliveTask = Task.Run(async () => await KeepaliveLoopAsync(_keepaliveCts.Token));
            _logger.LogInformation("✅ 连接保活机制已启动 (DataChannel: {DcInterval}ms)", 
                DATACHANNEL_KEEPALIVE_INTERVAL_MS);
        }
        
        public void SetKeepaliveDataChannel(RTCDataChannel dataChannel)
        {
            if (dataChannel == null)
            {
                return;
            }
            
            lock (_dataChannelLock)
            {
                if (_keepaliveDataChannel != null)
                {
                    try
                    {
                        _keepaliveDataChannel.close();
                    }
                    catch { }
                }
                
                _keepaliveDataChannel = dataChannel;
                _dataChannelOpen = false;
                
                dataChannel.onopen += () =>
                {
                    lock (_dataChannelLock)
                    {
                        _dataChannelOpen = true;
                    }
                    _logger.LogInformation("✅ Keepalive DataChannel 已打开，开始心跳");
                };
                
                dataChannel.onclose += () =>
                {
                    lock (_dataChannelLock)
                    {
                        _dataChannelOpen = false;
                        _keepaliveDataChannel = null;
                    }
                    _logger.LogWarning("⚠️ Keepalive DataChannel 已关闭");
                };
                
                dataChannel.onerror += (error) =>
                {
                    _logger.LogWarning("⚠️ Keepalive DataChannel 错误: {Error}", error);
                };
                
                _logger.LogInformation("✅ Keepalive DataChannel 已设置");
            }
        }
        
        private void CheckKeepaliveDataChannel()
        {
            lock (_dataChannelLock)
            {
                if (_keepaliveDataChannel != null)
                {
                    return;
                }
            }
            
            try
            {
                if (_peerConnection == null || _disposed)
                {
                    return;
                }
                
                _peerConnection.ondatachannel += (channel) =>
                {
                    if (channel.label == "keepalive")
                    {
                        SetKeepaliveDataChannel(channel);
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 检查 Keepalive DataChannel 时出错");
            }
        }
        
        public void StopKeepalive()
        {
            try
            {
                _keepaliveCts?.Cancel();
                if (_keepaliveTask != null)
                {
                    try
                    {
                        _keepaliveTask.Wait(TimeSpan.FromMilliseconds(500));
                    }
                    catch { }
                }
                _keepaliveCts?.Dispose();
                _keepaliveCts = null;
                _keepaliveTask = null;
                
                // ✅ 同时停止 TURN keepalive
                StopTurnKeepalive();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 停止保活机制时出错");
            }
        }
        
        private async Task KeepaliveLoopAsync(CancellationToken ct)
        {
            DateTime lastDataChannelKeepalive = DateTime.MinValue;
            DateTime lastSilentAudioKeepalive = DateTime.MinValue;
            
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // ✅ 提高检查频率：从1秒改为500ms，更快响应keepalive需求
                    await Task.Delay(500, ct);
                    
                    if (ct.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        if (_peerConnection == null || _disposed)
                        {
                            break;
                        }
                        
                        var (connectionState, iceState, signalingState) = GetCachedConnectionState();
                        
                        if (connectionState != RTCPeerConnectionState.connected ||
                            iceState != RTCIceConnectionState.connected)
                        {
                            continue;
                        }
                        
                        var now = DateTime.UtcNow;
                        var timeSinceLastPacket = (now - _lastVideoOrAudioPacketTime).TotalMilliseconds;
                        
                        // ✅ TURN keepalive 现在由独立的 StartTurnKeepalive() 循环处理
                        // 不再需要在这里调用 SendStunBindingRequest()
                        
                        bool dataChannelKeepaliveNeeded = false;
                        bool dataChannelAvailable = false;
                        lock (_dataChannelLock)
                        {
                            dataChannelAvailable = _keepaliveDataChannel != null && _dataChannelOpen;
                            if (dataChannelAvailable)
                            {
                                var timeSinceLastDcKeepalive = (now - lastDataChannelKeepalive).TotalMilliseconds;
                                // ✅ 修复：即使有数据包传输，也保持定期keepalive（5秒间隔）
                                // 这对于TURN连接特别重要，因为NAT映射可能在没有keepalive时过期
                                // 不再根据数据包传输情况延长keepalive间隔
                                    dataChannelKeepaliveNeeded = timeSinceLastDcKeepalive >= DATACHANNEL_KEEPALIVE_INTERVAL_MS;
                            }
                        }
                        
                        if (dataChannelKeepaliveNeeded && dataChannelAvailable)
                        {
                            bool sent = false;
                            lock (_dataChannelLock)
                            {
                                if (_keepaliveDataChannel != null && _dataChannelOpen)
                                {
                                    try
                                    {
                                        _keepaliveDataChannel.send(new byte[] { 0x00 });
                                        sent = true;
                                        lastDataChannelKeepalive = now;
                                        _lastKeepaliveTime = now;
                                        // 仅在调试模式下记录，避免日志过多
                                        if (_videoPacketCount % 1000 == 0)
                                        {
                                            _logger.LogDebug("📤 发送 DataChannel keepalive (间隔: {Interval}ms)", 
                                                DATACHANNEL_KEEPALIVE_INTERVAL_MS);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "⚠️ DataChannel keepalive 发送失败");
                                        lock (_dataChannelLock)
                                        {
                                            _dataChannelOpen = false;
                                        }
                                    }
                                }
                            }
                            
                            if (sent)
                            {
                                continue;
                            }
                        }
                        
                        if (!dataChannelAvailable)
                        {
                            var timeSinceLastSilentAudio = (now - lastSilentAudioKeepalive).TotalMilliseconds;
                            // ✅ 缩短静音音频keepalive间隔：从30秒改为15秒，提高TURN连接稳定性
                            if (timeSinceLastSilentAudio >= 15000 && timeSinceLastPacket >= 15000)
                            {
                                try
                                {
                                    SendSilentAudioKeepalive();
                                    lastSilentAudioKeepalive = now;
                                    _lastKeepaliveTime = now;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "⚠️ 静音音频 keepalive 发送失败");
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 发送 keepalive 包时出错");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 保活循环异常");
            }
            finally
            {
                _logger.LogDebug("🛑 保活循环已退出");
            }
        }
        
        /// <summary>
        /// ✅ 构造 STUN Binding Request 包
        /// STUN 消息格式（RFC 5389）：
        /// - 前 2 字节：消息类型（0x0001 = Binding Request）
        /// - 第 3-4 字节：消息长度（0x0000，无属性）
        /// - 第 5-8 字节：魔术 Cookie（固定值 0x2112A442）
        /// - 第 9-20 字节：事务 ID（12 字节随机值）
        /// </summary>
        private byte[] BuildStunBindingRequest()
        {
            var buffer = new byte[20]; // STUN Binding Request 最小长度：20 字节
            
            // 消息类型：Binding Request (0x0001)
            // 注意：STUN 消息类型的高位是消息类别（Request = 0b00），低位是方法（Binding = 0x0001）
            buffer[0] = 0x00;
            buffer[1] = 0x01;
            
            // 消息长度：0（无属性）
            buffer[2] = 0x00;
            buffer[3] = 0x00;
            
            // 魔术 Cookie（固定值 0x2112A442）
            buffer[4] = 0x21;
            buffer[5] = 0x12;
            buffer[6] = 0xA4;
            buffer[7] = 0x42;
            
            // 事务 ID（12 字节随机值）
            var random = new Random();
            random.NextBytes(buffer.AsSpan(8, 12));
            
            return buffer;
        }
        
        /// <summary>
        /// ✅ 提取 TURN relay candidate 并启动 TURN keepalive
        /// 这是 WebRTC 服务器端（ICE-Lite）必须主动发送的 keepalive
        /// 因为 ICE-Lite 服务器不会自动发送 STUN Binding Request
        /// </summary>
        private void ExtractTurnRelayAndStartKeepalive()
        {
            try
            {
                if (_peerConnection == null || _disposed)
                {
                    return;
                }
                
                // ✅ 从 local description 的 SDP 中提取服务器端的 TURN relay candidate
                // 注意：remote description 包含的是客户端的 relay candidate
                // 我们需要服务器端的 relay candidate（在 local description 中）
                var localDesc = _peerConnection.localDescription;
                if (localDesc?.sdp == null)
                {
                    // 如果 local description 还没有，尝试 remote description（作为后备）
                    localDesc = _peerConnection.remoteDescription;
                }
                
                if (localDesc?.sdp == null)
                {
                    _logger.LogWarning("⚠️ 无法获取 SDP description，无法提取 TURN relay candidate");
                    return;
                }
                
                var sdp = localDesc.sdp.ToString();
                if (string.IsNullOrWhiteSpace(sdp))
                {
                    return;
                }
                
                _logger.LogDebug("🔍 从 {Source} 提取 TURN relay candidate", 
                    localDesc == _peerConnection.localDescription ? "localDescription" : "remoteDescription");
                
                // ✅ 解析 SDP 中的 relay candidate（typ relay）
                // 格式示例: a=candidate:1 1 UDP 2130706431 192.168.1.100 54321 typ relay raddr 192.168.1.1 rport 12345
                // 或: a=candidate:1 1 TCP 2130706431 192.168.1.100 54321 typ relay raddr 192.168.1.1 rport 12345
                var lines = sdp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? relayAddress = null;
                int? relayPort = null;
                string? relayProtocol = null;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("a=candidate:") && trimmed.Contains("typ relay"))
                    {
                        _logger.LogDebug("🔍 找到 relay candidate 行: {Line}", trimmed);
                        
                        // 解析 candidate 行
                        var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] == "typ" && i + 1 < parts.Length && parts[i + 1] == "relay")
                            {
                                // 找到 relay candidate，提取地址和端口
                                // candidate 格式: foundation component protocol priority address port typ ...
                                if (parts.Length >= 6)
                                {
                                    relayProtocol = parts[2]; // UDP 或 TCP
                                    relayAddress = parts[4]; // address
                                    if (int.TryParse(parts[5], out var port))
                                    {
                                        relayPort = port;
                                    }
                                    _logger.LogDebug("🔍 解析到 relay candidate: {Protocol} {Address}:{Port}", 
                                        relayProtocol, relayAddress, relayPort);
                                }
                                break;
                            }
                        }
                        if (relayAddress != null && relayPort.HasValue)
                        {
                            break;
                        }
                    }
                }
                
                if (relayAddress != null && relayPort.HasValue)
                {
                    try
                    {
                        var ipAddress = IPAddress.Parse(relayAddress);
                        _turnRelay = new IPEndPoint(ipAddress, relayPort.Value);
                        _turnProtocol = relayProtocol?.ToUpperInvariant() ?? "UDP";
                        
                        _logger.LogInformation("✅ 提取到 TURN relay candidate: {Protocol} {Relay} (从 {Source})", 
                            _turnProtocol, _turnRelay, localDesc == _peerConnection.localDescription ? "localDescription" : "remoteDescription");
                        
                        // ✅ 启动 TURN keepalive（根据协议类型使用对应的 socket）
                        StartTurnKeepalive();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析 TURN relay 地址失败: {Address}:{Port}", relayAddress, relayPort);
                    }
                }
                else
                {
                    // 没有找到 relay candidate，可能不是 TURN 连接
                    _logger.LogDebug("ℹ️ 未找到 TURN relay candidate，可能使用直接连接或 STUN。SDP 预览: {SdpPreview}", 
                        sdp.Length > 200 ? sdp.Substring(0, 200) + "..." : sdp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 提取 TURN relay candidate 失败");
            }
        }
        
        /// <summary>
        /// ✅ 启动 TURN keepalive 循环
        /// 每 5 秒向 TURN relay 地址发送 STUN Binding Request
        /// 根据协议类型（UDP/TCP）使用对应的 socket
        /// </summary>
        private void StartTurnKeepalive()
        {
            if (_turnRelay == null || string.IsNullOrEmpty(_turnProtocol))
            {
                return;
            }
            
            // 停止现有的 keepalive
            StopTurnKeepalive();
            
            try
            {
                _turnKeepaliveCts = new CancellationTokenSource();
                
                if (_turnProtocol == "UDP")
                {
                    // ✅ UDP TURN: 不使用独立 UDP socket，通过现有的 WebRTC 连接发送 keepalive
                    // ⚠️ 重要：创建独立的 UDP socket 会破坏 NAT 映射，导致黑屏
                    // ✅ 正确的做法：通过 DataChannel 心跳（5秒）和静音音频包（15秒）保持连接
                    // 这些包会通过现有的 WebRTC UDP socket 发送，保持 NAT 映射活跃
                    _logger.LogInformation("✅ UDP TURN keepalive 将通过 DataChannel 和静音音频包维持 (Relay: {Relay})", 
                        _turnRelay);
                    // 不需要启动独立的 keepalive 任务，DataChannel 和静音音频 keepalive 已经在 KeepaliveLoopAsync 中处理
                }
                else if (_turnProtocol == "TCP")
                {
                    // ✅ TCP TURN: 使用 TCP socket 连接到 relay 地址并发送
                    // 注意：对于 TCP TURN，我们需要连接到 relay 地址（TURN 服务器分配的地址）
                    _turnKeepaliveTask = Task.Run(async () =>
                    {
                        try
                        {
                            // 连接到 TCP relay 地址
                            if (_turnKeepaliveTcpSocket == null || !_turnKeepaliveTcpSocket.Connected)
                            {
                                _turnKeepaliveTcpSocket?.Close();
                                _turnKeepaliveTcpSocket = new TcpClient();
                                
                                if (_turnRelay != null)
                                {
                                    await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                    _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
                                    _logger.LogInformation("✅ TCP TURN keepalive socket 已连接到 {Relay}", _turnRelay);
                                }
                            }
                            
                            while (!_turnKeepaliveCts.Token.IsCancellationRequested)
                            {
                                if (_turnKeepaliveTcpStream != null && _turnKeepaliveTcpSocket != null && 
                                    _turnKeepaliveTcpSocket.Connected && !_disposed)
                                {
                                    try
                                    {
                                        var stunPacket = BuildStunBindingRequest();
                                        await _turnKeepaliveTcpStream.WriteAsync(stunPacket, 0, stunPacket.Length, _turnKeepaliveCts.Token);
                                        await _turnKeepaliveTcpStream.FlushAsync(_turnKeepaliveCts.Token);
                                        
                                        // 每 20 次记录一次日志（避免日志过多）
                                        if (_videoPacketCount % 20 == 0)
                                        {
                                            _logger.LogDebug("✅ TCP TURN keepalive 已发送到 {Relay}", _turnRelay);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "⚠️ TCP TURN keepalive 发送失败: {Relay}，尝试重连", _turnRelay);
                                        
                                        // 尝试重新连接
                                        try
                                        {
                                            _turnKeepaliveTcpSocket?.Close();
                                            _turnKeepaliveTcpSocket = new TcpClient();
                                            if (_turnRelay != null)
                                            {
                                                await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                                _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
                                                _logger.LogInformation("✅ TCP TURN keepalive socket 已重新连接到 {Relay}", _turnRelay);
                                            }
                                        }
                                        catch (Exception reconnectEx)
                                        {
                                            _logger.LogWarning(reconnectEx, "⚠️ TCP TURN keepalive 重连失败: {Relay}", _turnRelay);
                                        }
                                    }
                                }
                                else
                                {
                                    // Socket 未连接，尝试重连
                                    try
                                    {
                                        _turnKeepaliveTcpSocket?.Close();
                                        _turnKeepaliveTcpSocket = new TcpClient();
                                        if (_turnRelay != null)
                                        {
                                            await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                            _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
                                            _logger.LogInformation("✅ TCP TURN keepalive socket 已重新连接到 {Relay}", _turnRelay);
                                        }
                                    }
                                    catch (Exception reconnectEx)
                                    {
                                        _logger.LogWarning(reconnectEx, "⚠️ TCP TURN keepalive 重连失败: {Relay}", _turnRelay);
                                    }
                                }
                                
                                await Task.Delay(TURN_KEEPALIVE_INTERVAL_MS, _turnKeepaliveCts.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // 正常取消
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ TCP TURN keepalive 循环异常");
                        }
                    }, _turnKeepaliveCts.Token);
                    
                    _logger.LogInformation("✅ TCP TURN keepalive 已启动 (间隔: {Interval}ms, Relay: {Relay})", 
                        TURN_KEEPALIVE_INTERVAL_MS, _turnRelay);
                }
                else
                {
                    _logger.LogWarning("⚠️ 未知的 TURN 协议类型: {Protocol}，无法启动 keepalive", _turnProtocol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 启动 TURN keepalive 失败");
            }
        }
        
        /// <summary>
        /// ✅ 停止 TURN keepalive
        /// </summary>
        private void StopTurnKeepalive()
        {
            try
            {
                _turnKeepaliveCts?.Cancel();
                if (_turnKeepaliveTask != null)
                {
                    try
                    {
                        _turnKeepaliveTask.Wait(TimeSpan.FromMilliseconds(500));
                    }
                    catch { }
                }
                _turnKeepaliveCts?.Dispose();
                _turnKeepaliveCts = null;
                _turnKeepaliveTask = null;
                
                // ⚠️ UDP TURN 不使用独立 socket，无需清理
                // 清理 TCP socket（仅用于 TCP TURN）
                try
                {
                    _turnKeepaliveTcpStream?.Close();
                }
                catch { }
                _turnKeepaliveTcpStream = null;
                
                try
                {
                    _turnKeepaliveTcpSocket?.Close();
                }
                catch { }
                _turnKeepaliveTcpSocket = null;
                
                _turnRelay = null;
                _turnProtocol = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 停止 TURN keepalive 时出错");
            }
        }
        
        private void SendSilentAudioKeepalive()
        {
            try
            {
                if (_peerConnection == null || _disposed || _audioTrack == null)
                {
                    return;
                }
                
                var silentOpus = new byte[] { 0xF8, 0xFF, 0xFE };
                SendAudioOpusDirect(silentOpus, 480);
            }
            catch (Exception ex)
            {
                if (_videoPacketCount % 1000 == 0)
                {
                    _logger.LogDebug(ex, "⚠️ 发送静音音频 keepalive 失败");
                }
            }
        }
    }
}

