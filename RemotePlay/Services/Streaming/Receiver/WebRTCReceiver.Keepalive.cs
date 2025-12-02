using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed partial class WebRTCReceiver
    {
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
                        _logger.LogInformation("✅ Keepalive DataChannel 已打开");
                    }
                };
                
                dataChannel.onclose += () =>
                {
                    lock (_dataChannelLock)
                    {
                        _dataChannelOpen = false;
                        _keepaliveDataChannel = null;
                        _logger.LogInformation("⚠️ Keepalive DataChannel 已关闭");
                    }
                };
                
                dataChannel.onerror += (error) =>
                {
                    _logger.LogWarning("Keepalive DataChannel 错误: {Error}", error);
                };
                
                // ✅ 接收前端的 pong 响应 (0x02)
                dataChannel.onmessage += (_,__,data) =>
                {
                    try
                    {
                        if (data != null && data.Length > 0)
                        {
                            byte messageType = data[0];
                            if (messageType == 0x02) // PONG
                            {
                                _logger.LogDebug("📥 收到前端 PONG 响应");
                                _lastKeepaliveTime = DateTime.UtcNow;
                            }
                            else
                            {
                                _logger.LogDebug("📥 收到未知 DataChannel 消息类型: 0x{Type:X2}", messageType);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "处理 DataChannel 消息时出错");
                    }
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止保活机制时出错");
            }
        }
        
        private async Task KeepaliveLoopAsync(CancellationToken ct)
        {
            DateTime lastDataChannelKeepalive = DateTime.MinValue;
            DateTime lastSilentAudioPacket = DateTime.MinValue;
            
            try
            {
                // ✅ 如果有 audio track，必须每 20ms 持续发送静音包（音频流的必需数据）
                bool hasAudioTrack = _audioTrack != null;
                const int SILENT_AUDIO_INTERVAL_MS = 20; // 20ms = 50fps，音频流的必需频率
                
                while (!ct.IsCancellationRequested)
                {
                    var delayMs = hasAudioTrack ? SILENT_AUDIO_INTERVAL_MS : 500;
                    await Task.Delay(delayMs, ct);
                    
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
                        
                        // ✅ 如果有 audio track，必须每 20ms 持续发送静音包（音频流的必需数据，不是 keepalive）
                        if (hasAudioTrack)
                        {
                            var timeSinceLastSilentAudio = (now - lastSilentAudioPacket).TotalMilliseconds;
                            if (timeSinceLastSilentAudio >= SILENT_AUDIO_INTERVAL_MS)
                            {
                                try
                                {
                                    SendSilentAudioPacket();
                                    lastSilentAudioPacket = now;
                                    _lastKeepaliveTime = now;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "发送静音音频包失败");
                                }
                            }
                        }
                        
                        // ✅ DataChannel keepalive（如果有 DataChannel，每 5 秒发送，单向）
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
                                        // ✅ 发送 PING (0x01)，等待前端返回 PONG (0x02)
                                        _keepaliveDataChannel.send(new byte[] { 0x01 });
                                        sent = true;
                                        lastDataChannelKeepalive = now;
                                        _lastKeepaliveTime = now;
                                        _logger.LogDebug("📤 发送 DataChannel PING");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "DataChannel PING 发送失败");
                                        lock (_dataChannelLock)
                                        {
                                            _dataChannelOpen = false;
                                        }
                                    }
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
        
        /// <summary>
        /// 发送静音音频包（音频流的必需数据，每 20ms 持续发送）
        /// 这不是 keepalive，而是 audio track 的必需数据流
        /// </summary>
        private void SendSilentAudioPacket()
        {
            try
            {
                if (_peerConnection == null || _disposed || _audioTrack == null)
                {
                    return;
                }
                
                // Opus 静音帧（10ms @ 48kHz）
                var silentOpus = new byte[] { 0xF8, 0xFF, 0xFE };
                SendAudioOpusDirect(silentOpus, 480);
            }
            catch (Exception ex)
            {
                if (_videoPacketCount % 1000 == 0)
                {
                    _logger.LogDebug(ex, "发送静音音频包失败");
                }
            }
        }
    }
}

