using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver Keepalive 机制部分
    /// </summary>
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
                    await Task.Delay(1000, ct);
                    
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
                        
                        bool dataChannelKeepaliveNeeded = false;
                        bool dataChannelAvailable = false;
                        lock (_dataChannelLock)
                        {
                            dataChannelAvailable = _keepaliveDataChannel != null && _dataChannelOpen;
                            if (dataChannelAvailable)
                            {
                                var timeSinceLastDcKeepalive = (now - lastDataChannelKeepalive).TotalMilliseconds;
                                if (timeSinceLastPacket < DATACHANNEL_KEEPALIVE_INTERVAL_MS / 2)
                                {
                                    dataChannelKeepaliveNeeded = timeSinceLastDcKeepalive >= DATACHANNEL_KEEPALIVE_INTERVAL_MS * 2;
                                }
                                else
                                {
                                    dataChannelKeepaliveNeeded = timeSinceLastDcKeepalive >= DATACHANNEL_KEEPALIVE_INTERVAL_MS;
                                }
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
                            if (timeSinceLastSilentAudio >= 30000 && timeSinceLastPacket >= 30000)
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
