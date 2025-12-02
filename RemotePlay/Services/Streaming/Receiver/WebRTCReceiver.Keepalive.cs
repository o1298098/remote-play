using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver Keepalive 机制部分
    /// </summary>
    public sealed partial class WebRTCReceiver
    {
        // ✅ STUN Binding Request keepalive（用于 TURN 连接）
        private DateTime _lastStunKeepalive = DateTime.MinValue;
        private const int STUN_KEEPALIVE_INTERVAL_MS = 5000; // STUN Binding Request: 5秒（TURN连接需要STUN keepalive，不是DataChannel）
        private List<(string host, int port, string protocol)>? _turnServers; // 缓存的 TURN 服务器列表
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
                        
                        // ✅ 优先发送 STUN Binding Request keepalive（TURN连接需要）
                        var timeSinceLastStunKeepalive = (now - _lastStunKeepalive).TotalMilliseconds;
                        if (timeSinceLastStunKeepalive >= STUN_KEEPALIVE_INTERVAL_MS)
                        {
                            try
                            {
                                SendStunBindingRequest();
                                _lastStunKeepalive = now;
                                _lastKeepaliveTime = now;
                                // 仅在调试模式下记录，避免日志过多
                                if (_videoPacketCount % 1000 == 0)
                                {
                                    _logger.LogDebug("📤 发送 STUN Binding Request keepalive (间隔: {Interval}ms)", 
                                        STUN_KEEPALIVE_INTERVAL_MS);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "⚠️ STUN Binding Request keepalive 发送失败");
                            }
                        }
                        
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
        /// ✅ 发送 STUN Binding Request 作为 TURN keepalive
        /// 这是 TURN 服务器能识别的 keepalive 包，用于保持 TURN allocation 活跃
        /// 尝试通过反射访问 SIPSorcery 内部的传输通道来发送 STUN Binding Request
        /// 
        /// 注意：
        /// - TURN 连接可能使用 UDP 或 TCP 协议
        /// - UDP TURN: STUN Binding Request 通过 UDP socket 发送
        /// - TCP TURN: STUN Binding Request 通过 TCP socket/stream 发送（格式相同，但通过 TCP 传输）
        /// - 两种协议都需要定期发送 STUN Binding Request 以保持 allocation 活跃
        /// </summary>
        private void SendStunBindingRequest()
        {
            try
            {
                if (_peerConnection == null || _disposed)
                {
                    return;
                }
                
                // ✅ 方法1: 通过反射访问 RTCPeerConnection 内部的 ICE agent，发送 STUN Binding Request
                var peerConnectionType = _peerConnection.GetType();
                
                // 尝试多种可能的字段/属性名称
                object? iceAgent = null;
                string? foundFieldName = null;
                
                // 方法1: 查找所有字段（包括不包含 "ice" 的，因为可能名称不同）
                var fields = peerConnectionType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                foreach (var field in fields)
                {
                    var fieldName = field.Name.ToLowerInvariant();
                    // 放宽搜索条件：查找包含 "ice"、"transport"、"agent"、"connection" 的字段
                    if (fieldName.Contains("ice") || fieldName.Contains("transport") || 
                        fieldName.Contains("agent") || fieldName.Contains("connection"))
                    {
                        try
                        {
                            var value = field.GetValue(_peerConnection);
                            if (value != null)
                            {
                                iceAgent = value;
                                foundFieldName = field.Name;
                                // 仅在第一次找到时记录（避免日志过多）
                                if (_videoPacketCount % 200 == 0)
                                {
                                    _logger.LogDebug("🔍 通过反射找到字段: {FieldName} (类型: {Type})", 
                                        field.Name, value.GetType().Name);
                                }
                                break;
                            }
                        }
                        catch { }
                    }
                }
                
                // 方法2: 查找属性
                if (iceAgent == null)
                {
                    var properties = peerConnectionType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    foreach (var prop in properties)
                    {
                        var propName = prop.Name.ToLowerInvariant();
                        if (propName.Contains("ice") || propName.Contains("transport") || 
                            propName.Contains("agent") || propName.Contains("connection"))
                        {
                            try
                            {
                                var value = prop.GetValue(_peerConnection);
                                if (value != null)
                                {
                                    iceAgent = value;
                                    foundFieldName = prop.Name;
                                    if (_videoPacketCount % 200 == 0)
                                    {
                                        _logger.LogDebug("🔍 通过反射找到属性: {PropName} (类型: {Type})", 
                                            prop.Name, value.GetType().Name);
                                    }
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                
                if (iceAgent != null)
                {
                    // ✅ 尝试方法1: 调用 ICE agent 的发送 STUN Binding Request 方法
                    var iceAgentType = iceAgent.GetType();
                    var methods = iceAgentType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    // 查找可能的发送方法
                    MethodInfo? sendMethod = null;
                    foreach (var method in methods)
                    {
                        var methodName = method.Name.ToLowerInvariant();
                        if ((methodName.Contains("binding") || methodName.Contains("stun") || methodName.Contains("keepalive")) &&
                            (methodName.Contains("send") || methodName.Contains("request")))
                        {
                            sendMethod = method;
                            break;
                        }
                    }
                    
                    if (sendMethod != null)
                    {
                        try
                        {
                            // 调用方法（可能是异步的）
                            var result = sendMethod.Invoke(iceAgent, null);
                            if (result is Task task)
                            {
                                // 异步方法，不等待完成（fire and forget）
                                _ = task.ContinueWith(t =>
                                {
                                    if (t.IsFaulted)
                                    {
                                        _logger.LogWarning("⚠️ STUN Binding Request 发送异常: {Error}", t.Exception?.GetBaseException()?.Message);
                                    }
                                }, TaskContinuationOptions.OnlyOnFaulted);
                            }
                            // ✅ 成功调用，记录日志（每 20 次记录一次，避免日志过多）
                            if (_videoPacketCount % 20 == 0)
                            {
                                _logger.LogDebug("✅ STUN Binding Request keepalive 已发送（通过反射调用 ICE agent）");
                            }
                            return; // 成功调用，返回
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ 调用 ICE agent 发送 STUN Binding Request 失败");
                        }
                    }
                    
                    // ✅ 尝试方法2: 查找传输通道（UDP/TCP socket）并直接发送 STUN 包
                    // 注意：TURN 连接可能使用 UDP 或 TCP，需要同时支持两种协议
                    object? transport = null;
                    var transportFields = iceAgentType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    foreach (var field in transportFields)
                    {
                        var fieldName = field.Name.ToLowerInvariant();
                        // ✅ 同时搜索 UDP 和 TCP socket/transport
                        if (fieldName.Contains("transport") || fieldName.Contains("socket") || 
                            fieldName.Contains("udp") || fieldName.Contains("tcp") ||
                            fieldName.Contains("connection") || fieldName.Contains("stream"))
                        {
                            try
                            {
                                var value = field.GetValue(iceAgent);
                                if (value != null)
                                {
                                    transport = value;
                                    if (_videoPacketCount % 200 == 0)
                                    {
                                        _logger.LogDebug("🔍 找到传输通道字段: {FieldName} (类型: {Type})", 
                                            field.Name, value.GetType().Name);
                                    }
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    
                    // ✅ 如果字段搜索失败，尝试搜索集合类型的字段（可能有多个传输通道）
                    if (transport == null)
                    {
                        foreach (var field in transportFields)
                        {
                            var fieldName = field.Name.ToLowerInvariant();
                            if (fieldName.Contains("transport") || fieldName.Contains("connection") || 
                                fieldName.Contains("channel") || fieldName.Contains("socket"))
                            {
                                try
                                {
                                    var value = field.GetValue(iceAgent);
                                    if (value != null)
                                    {
                                        var valueType = value.GetType();
                                        // 检查是否是集合类型（List, Dictionary, Array 等）
                                        if (valueType.IsGenericType || valueType.IsArray)
                                        {
                                            // 尝试获取第一个元素
                                            if (value is System.Collections.IEnumerable enumerable)
                                            {
                                                foreach (var item in enumerable)
                                                {
                                                    if (item != null)
                                                    {
                                                        transport = item;
                                                        if (_videoPacketCount % 200 == 0)
                                                        {
                                                            _logger.LogDebug("🔍 从集合中找到传输通道: {FieldName} (类型: {Type})", 
                                                                field.Name, item.GetType().Name);
                                                        }
                                                        break;
                                                    }
                                                }
                                                if (transport != null) break;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    
                    if (transport != null)
                    {
                        var transportType = transport.GetType();
                        var sendMethods = transportType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        
                        // 查找 Send 或 SendAsync 方法
                        MethodInfo? transportSendMethod = null;
                        foreach (var method in sendMethods)
                        {
                            var methodName = method.Name.ToLowerInvariant();
                            if (methodName.Contains("send"))
                            {
                                var parameters = method.GetParameters();
                                // 查找接受 byte[] 或 byte[] 和 EndPoint 的方法
                                if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(byte[]))
                                {
                                    transportSendMethod = method;
                                    break;
                                }
                            }
                        }
                        
                        if (transportSendMethod != null)
                        {
                            try
                            {
                                var stunPacket = BuildStunBindingRequest();
                                
                                // 尝试调用 Send 方法
                                object? result = null;
                                if (transportSendMethod.GetParameters().Length == 1)
                                {
                                    result = transportSendMethod.Invoke(transport, new object[] { stunPacket });
                                }
                                else if (transportSendMethod.GetParameters().Length == 2)
                                {
                                    // 可能需要 EndPoint 参数，尝试从 ICE candidate 获取
                                    // 这里先尝试 null 或默认值
                                    result = transportSendMethod.Invoke(transport, new object[] { stunPacket, null! });
                                }
                                
                                if (result is Task task)
                                {
                                    _ = task.ContinueWith(t =>
                                    {
                                        if (t.IsFaulted)
                                        {
                                            _logger.LogWarning("⚠️ STUN Binding Request 发送异常: {Error}", t.Exception?.GetBaseException()?.Message);
                                        }
                                    }, TaskContinuationOptions.OnlyOnFaulted);
                                }
                                
                                // ✅ 成功发送
                                if (_videoPacketCount % 20 == 0)
                                {
                                    _logger.LogDebug("✅ STUN Binding Request keepalive 已发送（通过传输通道）");
                                }
                                return;
                            }
                            catch (Exception ex)
                            {
                                if (_videoPacketCount % 100 == 0)
                                {
                                    _logger.LogWarning(ex, "⚠️ 通过传输通道发送 STUN Binding Request 失败");
                                }
                            }
                        }
                    }
                    
                    // ✅ 找到 ICE agent 但找不到发送方法
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 找到 ICE agent ({Type}) 但未找到 STUN Binding Request 发送方法", iceAgentType.Name);
                    }
                }
                else
                {
                    // ✅ 未找到 ICE agent，尝试列出所有字段和属性（用于调试）
                    if (_videoPacketCount % 200 == 0)
                    {
                        var allFields = peerConnectionType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        var allProperties = peerConnectionType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        
                        var fieldNames = string.Join(", ", allFields.Take(10).Select(f => f.Name));
                        var propNames = string.Join(", ", allProperties.Take(10).Select(p => p.Name));
                        
                        _logger.LogWarning("⚠️ 无法通过反射找到 ICE agent。RTCPeerConnection 字段示例: {Fields}，属性示例: {Properties}", 
                            fieldNames, propNames);
                    }
                    else if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 无法通过反射找到 ICE agent，STUN Binding Request keepalive 可能无法发送");
                    }
                }
                
                // ✅ 方法2: 如果反射失败，无法直接发送 STUN Binding Request
                // 注意：SIPSorcery 的 RTCPeerConnection 没有 getStats() 方法
                // 如果反射方法失败，只能依赖 SIPSorcery 内部的自动 keepalive 机制
                // ⚠️ 警告：SIPSorcery 的默认 STUN keepalive 间隔是 15 秒，对于 TURN 连接可能太长了
                // 建议：如果反射失败，考虑实现一个独立的 STUN 客户端来发送 Binding Request
                if (_videoPacketCount % 100 == 0)
                {
                    _logger.LogWarning("⚠️ STUN Binding Request keepalive 反射失败，将依赖 SIPSorcery 内部机制（可能间隔过长）");
                }
            }
            catch (Exception ex)
            {
                // 静默失败，避免影响主流程
                if (_videoPacketCount % 1000 == 0)
                {
                    _logger.LogDebug(ex, "⚠️ 发送 STUN Binding Request 失败");
                }
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

