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
        private IPEndPoint? _turnRelay;
        private string? _turnProtocol;
        private UdpClient? _turnKeepaliveUdpSocket;
        private TcpClient? _turnKeepaliveTcpSocket;
        private NetworkStream? _turnKeepaliveTcpStream;
        private CancellationTokenSource? _turnKeepaliveCts;
        private Task? _turnKeepaliveTask;
        private const int TURN_KEEPALIVE_INTERVAL_MS = 5000;
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
                };
                
                dataChannel.onclose += () =>
                {
                    lock (_dataChannelLock)
                    {
                        _dataChannelOpen = false;
                        _keepaliveDataChannel = null;
                    }
                };
                
                dataChannel.onerror += (error) =>
                {
                    _logger.LogWarning("Keepalive DataChannel 错误: {Error}", error);
                };
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
                _logger.LogWarning(ex, "检查 Keepalive DataChannel 时出错");
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
                
                StopTurnKeepalive();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止保活机制时出错");
            }
        }
        
        /// <summary>
        /// WebRTC Keepalive 主循环
        /// - DataChannel keepalive: 每 5 秒发送 1 字节心跳
        /// - 静音音频包: 每 15 秒发送（当 DataChannel 不可用时）
        /// </summary>
        private async Task KeepaliveLoopAsync(CancellationToken ct)
        {
            DateTime lastDataChannelKeepalive = DateTime.MinValue;
            DateTime lastSilentAudioKeepalive = DateTime.MinValue;
            
            try
            {
                while (!ct.IsCancellationRequested)
                {
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
                        
                        bool dataChannelKeepaliveNeeded = false;
                        bool dataChannelAvailable = false;
                        lock (_dataChannelLock)
                        {
                            dataChannelAvailable = _keepaliveDataChannel != null && _dataChannelOpen;
                            if (dataChannelAvailable)
                            {
                                var timeSinceLastDcKeepalive = (now - lastDataChannelKeepalive).TotalMilliseconds;
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
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "DataChannel keepalive 发送失败");
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
                            const int SILENT_AUDIO_KEEPALIVE_INTERVAL_MS = 15000;
                            
                            if (timeSinceLastSilentAudio >= SILENT_AUDIO_KEEPALIVE_INTERVAL_MS)
                            {
                                try
                                {
                                    SendSilentAudioKeepalive();
                                    lastSilentAudioKeepalive = now;
                                    _lastKeepaliveTime = now;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "静音音频 keepalive 发送失败");
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
                        _logger.LogWarning(ex, "发送 keepalive 包时出错");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保活循环异常");
            }
        }
        
        private byte[] BuildStunBindingRequest()
        {
            var buffer = new byte[20];
            buffer[0] = 0x00;
            buffer[1] = 0x01;
            buffer[2] = 0x00;
            buffer[3] = 0x00;
            buffer[4] = 0x21;
            buffer[5] = 0x12;
            buffer[6] = 0xA4;
            buffer[7] = 0x42;
            
            var random = new Random();
            random.NextBytes(buffer.AsSpan(8, 12));
            
            return buffer;
        }
        
        private void ExtractTurnRelayAndStartKeepalive()
        {
            try
            {
                if (_peerConnection == null || _disposed)
                {
                    return;
                }
                
                var localDesc = _peerConnection.localDescription;
                if (localDesc?.sdp == null)
                {
                    localDesc = _peerConnection.remoteDescription;
                }
                
                if (localDesc?.sdp == null)
                {
                    return;
                }
                
                var sdp = localDesc.sdp.ToString();
                if (string.IsNullOrWhiteSpace(sdp))
                {
                    return;
                }
                
                var lines = sdp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? relayAddress = null;
                int? relayPort = null;
                string? relayProtocol = null;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("a=candidate:") && trimmed.Contains("typ relay"))
                    {
                        var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] == "typ" && i + 1 < parts.Length && parts[i + 1] == "relay")
                            {
                                if (parts.Length >= 6)
                                {
                                    relayProtocol = parts[2];
                                    relayAddress = parts[4];
                                    if (int.TryParse(parts[5], out var port))
                                    {
                                        relayPort = port;
                                    }
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
                        
                        _logger.LogInformation("提取到 TURN relay candidate: {Protocol} {Relay}", 
                            _turnProtocol, _turnRelay);
                        
                        StartTurnKeepalive();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析 TURN relay 地址失败: {Address}:{Port}", relayAddress, relayPort);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "提取 TURN relay candidate 失败");
            }
        }
        
        private void StartTurnKeepalive()
        {
            if (_turnRelay == null || string.IsNullOrEmpty(_turnProtocol))
            {
                return;
            }
            
            StopTurnKeepalive();
            
            try
            {
                _turnKeepaliveCts = new CancellationTokenSource();
                
                if (_turnProtocol == "UDP")
                {
                    return;
                }
                else if (_turnProtocol == "TCP")
                {
                    _turnKeepaliveTask = Task.Run(async () =>
                    {
                        try
                        {
                            if (_turnKeepaliveTcpSocket == null || !_turnKeepaliveTcpSocket.Connected)
                            {
                                _turnKeepaliveTcpSocket?.Close();
                                _turnKeepaliveTcpSocket = new TcpClient();
                                
                                if (_turnRelay != null)
                                {
                                    await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                    _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
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
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "TCP TURN keepalive 发送失败，尝试重连: {Relay}", _turnRelay);
                                        
                                        try
                                        {
                                            _turnKeepaliveTcpSocket?.Close();
                                            _turnKeepaliveTcpSocket = new TcpClient();
                                            if (_turnRelay != null)
                                            {
                                                await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                                _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
                                            }
                                        }
                                        catch (Exception reconnectEx)
                                        {
                                            _logger.LogWarning(reconnectEx, "TCP TURN keepalive 重连失败: {Relay}", _turnRelay);
                                        }
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        _turnKeepaliveTcpSocket?.Close();
                                        _turnKeepaliveTcpSocket = new TcpClient();
                                        if (_turnRelay != null)
                                        {
                                            await _turnKeepaliveTcpSocket.ConnectAsync(_turnRelay.Address, _turnRelay.Port);
                                            _turnKeepaliveTcpStream = _turnKeepaliveTcpSocket.GetStream();
                                        }
                                    }
                                    catch (Exception reconnectEx)
                                    {
                                        _logger.LogWarning(reconnectEx, "TCP TURN keepalive 重连失败: {Relay}", _turnRelay);
                                    }
                                }
                                
                                await Task.Delay(TURN_KEEPALIVE_INTERVAL_MS, _turnKeepaliveCts.Token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "TCP TURN keepalive 循环异常");
                        }
                    }, _turnKeepaliveCts.Token);
                    
                    _logger.LogInformation("TCP TURN keepalive 已启动: {Relay}", _turnRelay);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 TURN keepalive 失败");
            }
        }
        
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
                
                _turnKeepaliveUdpSocket?.Dispose();
                _turnKeepaliveUdpSocket = null;
                
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
                _logger.LogWarning(ex, "停止 TURN keepalive 时出错");
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
                    _logger.LogDebug(ex, "发送静音音频 keepalive 失败");
                }
            }
        }
    }
}

