using SIPSorcery.Net;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed partial class WebRTCReceiver
    {
        // ✅ 发送失败统计（用于监控和诊断）
        private int _sendTimeoutCount = 0;
        private int _sendFailureCount = 0;
        private DateTime _lastSendFailureTime = DateTime.MinValue;
        
        /// <summary>
        /// 动态检测帧率并更新
        /// </summary>
        private void UpdateFrameRate(DateTime frameTime)
        {
            if (_lastVideoPacketTime == DateTime.MinValue)
            {
                return; // 第一帧，无法计算
            }
            
            var elapsed = (frameTime - _lastVideoPacketTime).TotalSeconds;
            
            // 过滤异常值（间隔太大或太小）
            if (elapsed > 0 && elapsed < 1.0)
            {
                // ✅ 修复：不在这里加锁，因为调用者已经持有锁
                // 记录帧间隔历史
                _frameIntervalHistory.Enqueue(elapsed);
                
                // 保持历史记录在合理大小
                while (_frameIntervalHistory.Count > FRAME_RATE_HISTORY_SIZE)
                {
                    _frameIntervalHistory.Dequeue();
                }
                
                // ✅ 优化：降低样本要求，加快初始化（从10降到5）
                // 定期更新检测到的帧率（避免频繁计算）
                var now = DateTime.UtcNow;
                if (_lastFrameRateUpdateTime == DateTime.MinValue || 
                    (now - _lastFrameRateUpdateTime).TotalMilliseconds >= FRAME_RATE_UPDATE_INTERVAL_MS)
                {
                    // ✅ 降低样本要求：从10降到5，加快帧率检测初始化
                    if (_frameIntervalHistory.Count >= 5) // 至少需要5个样本（约0.1秒@60fps）
                    {
                        // 计算平均帧间隔
                        double avgInterval = _frameIntervalHistory.Average();
                        
                        // 计算帧率（fps = 1 / interval）
                        double newFrameRate = 1.0 / avgInterval;
                        
                        // 限制在合理范围内
                        newFrameRate = Math.Max(MIN_FRAME_RATE, Math.Min(MAX_FRAME_RATE, newFrameRate));
                        
                        // 平滑更新（避免突然跳跃）
                        _detectedFrameRate = _detectedFrameRate * 0.7 + newFrameRate * 0.3; // 70% 旧值 + 30% 新值
                        
                        // 重新计算时间戳增量
                        _videoTimestampIncrement = VIDEO_CLOCK_RATE / _detectedFrameRate;
                        
                        _lastFrameRateUpdateTime = now;
                        
                        // 记录日志（限流）
                        if (_videoPacketCount % 100 == 0)
                        {
                            _logger.LogDebug("📊 检测到视频帧率: {FrameRate:F1} fps (时间戳增量: {Increment:F1}, 样本数: {Samples})", 
                                _detectedFrameRate, _videoTimestampIncrement, _frameIntervalHistory.Count);
                        }
                    }
                    else if (_frameIntervalHistory.Count > 0)
                    {
                        // ✅ 在样本不足时，使用临时计算的帧率（避免等待太久）
                        double tempInterval = _frameIntervalHistory.Average();
                        double tempFrameRate = 1.0 / tempInterval;
                        tempFrameRate = Math.Max(MIN_FRAME_RATE, Math.Min(MAX_FRAME_RATE, tempFrameRate));
                        
                        // 使用更大的新值权重，快速适应
                        _detectedFrameRate = _detectedFrameRate * 0.5 + tempFrameRate * 0.5; // 50% 旧值 + 50% 新值
                        _videoTimestampIncrement = VIDEO_CLOCK_RATE / _detectedFrameRate;
                    }
                }
            }
        }
        
        /// <summary>
        /// ✅ 统一时间戳管理：确保每帧时间戳只更新一次
        /// 基于实际帧间隔计算，处理时间戳回绕
        /// ⚠️ 临时简化：禁用动态帧率检测，使用固定增量作为后备
        /// </summary>
        private void UpdateVideoTimestamp(DateTime frameTime)
        {
            // ⚠️ 临时禁用动态帧率检测，避免可能的性能问题
            // UpdateFrameRate(frameTime);
            
            if (_lastVideoPacketTime != DateTime.MinValue)
            {
                var elapsed = (frameTime - _lastVideoPacketTime).TotalSeconds;
                if (elapsed > 0 && elapsed < 1.0)
                {
                    // 正常情况：基于实际时间间隔计算（最准确）
                    _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                }
                else
                {
                    // 异常情况：使用默认增量（临时简化，避免动态检测可能的问题）
                    _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT_DEFAULT;
                    
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 帧间隔异常 ({Elapsed:F3}s)，使用默认增量 ({Increment:F1})", 
                            elapsed, VIDEO_TIMESTAMP_INCREMENT_DEFAULT);
                    }
                }
            }
            else
            {
                // ✅ 第一帧：初始化时间戳
                _videoTimestamp = 0;
            }
            
            _lastVideoPacketTime = frameTime;
            
            // ✅ 处理时间戳回绕（32位约13小时后）
            // uint 最大值是 0xFFFFFFFF (4,294,967,295)
            // 90000 Hz 时钟下，约 13.3 小时后回绕
            if (_videoTimestamp > 0xFFFFFFFF - VIDEO_CLOCK_RATE)
            {
                _logger.LogInformation("🔄 视频时间戳即将回绕，重置为 0（当前值: {Timestamp}）", _videoTimestamp);
                _videoTimestamp = 0;
            }
        }
        
        /// <summary>
        /// 安全调用反射方法，带超时保护（防止 WebRTC 发送阻塞）
        /// ✅ 修复：返回是否成功，避免超时或失败时静默丢弃视频包
        /// ✅ 改进：增加重试机制，避免超时后立即丢弃
        /// </summary>
        private bool SafeInvokeMethod(Action invokeAction, string methodName, int timeoutMs = 100)
        {
            return SafeInvokeMethodWithRetry(invokeAction, methodName, timeoutMs, maxRetries: 1);
        }
        
        /// <summary>
        /// ✅ 修复：安全调用反射方法，带重试机制，避免 GetAwaiter().GetResult() 死锁
        /// 使用 ConfigureAwait(false) 和异步方式，避免在同步上下文中死锁
        /// </summary>
        private bool SafeInvokeMethodWithRetry(Action invokeAction, string methodName, int timeoutMs = 100, int maxRetries = 1)
        {
            // ✅ 修复：使用异步方式，避免 GetAwaiter().GetResult() 死锁
            // 注意：这个方法现在返回同步结果，但内部使用异步方式避免死锁
            try
            {
                return SafeInvokeMethodWithRetryAsync(invokeAction, methodName, timeoutMs, maxRetries)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                if (_videoPacketCount % 10 == 0)
                {
                    _logger.LogWarning(ex, "⚠️ {Method} 调用异常", methodName);
                }
                return false;
            }
        }
        
        /// <summary>
        /// 异步版本的安全调用方法，避免死锁
        /// </summary>
        private async Task<bool> SafeInvokeMethodWithRetryAsync(Action invokeAction, string methodName, int timeoutMs = 100, int maxRetries = 1)
        {
            for (int retry = 0; retry <= maxRetries; retry++)
            {
                var invokeTask = Task.Run(invokeAction);
                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(invokeTask, timeoutTask).ConfigureAwait(false);
                
                if (completedTask == timeoutTask)
                {
                    // 超时：如果是最后一次重试，返回失败；否则重试
                    if (retry < maxRetries)
                    {
                        // ✅ 优化：使用异步延迟，避免阻塞
                        await Task.Delay(5).ConfigureAwait(false);
                        continue;
                    }
                    
                    // 最后一次重试也超时，记录统计并返回失败
                    _sendTimeoutCount++;
                    if (_videoPacketCount % 10 == 0) // 限流日志
                    {
                        _logger.LogWarning("⚠️ {Method} 调用超时（{Timeout}ms，重试 {Retry}/{MaxRetries}），可能 WebRTC 发送阻塞，视频包可能丢失", 
                            methodName, timeoutMs, retry + 1, maxRetries + 1);
                    }
                    return false;
                }
                
                // 检查是否有异常
                if (invokeTask.IsFaulted)
                {
                    var ex = invokeTask.Exception?.InnerException ?? invokeTask.Exception ?? new Exception($"{methodName} failed");
                    
                    // ✅ 关键修复：不抛出异常，而是返回false，避免中断处理流程
                    // 如果是最后一次重试，记录异常并返回失败
                    if (retry >= maxRetries)
                    {
                        _sendFailureCount++;
                        _lastSendFailureTime = DateTime.UtcNow;
                        if (_videoPacketCount % 10 == 0)
                        {
                            _logger.LogWarning(ex, "⚠️ {Method} 调用失败（重试 {Retry}/{MaxRetries}），视频包可能丢失", 
                                methodName, retry + 1, maxRetries + 1);
                        }
                        return false; // ✅ 不抛出异常，返回失败
                    }
                    
                    // ✅ 优化：使用异步延迟，避免阻塞
                    await Task.Delay(5).ConfigureAwait(false);
                    continue;
                }
                
                // 成功：如果是重试后成功，记录日志
                if (retry > 0)
                {
                    _logger.LogDebug("✅ {Method} 重试成功（第 {Retry} 次重试）", methodName, retry);
                }
                
                return true; // 成功
            }
            
            return false; // 不应该到达这里
        }
        
        /// <summary>
        /// ✅ 优先发送IDR关键帧（用于关键帧优先处理）
        /// </summary>
        public void OnVideoPacketPriority(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    return;
                }

                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);

                if (_peerConnection == null)
                {
                    return;
                }

                // ✅ 使用新的模块化视频处理管道
                if (_videoPipeline != null)
                {
                    // ✅ 非阻塞异步发送
                    _ = _videoPipeline.OnIdrFrame(packet);
                    return;
                }
                
                // ⚠️ 如果管道未初始化，记录警告（限流：每 10 秒最多一次）
                var now = DateTime.UtcNow;
                if ((now - _lastVideoPipelineWarningTime).TotalSeconds >= VIDEO_PIPELINE_WARNING_INTERVAL_SECONDS)
                {
                    _logger.LogWarning("⚠️ 视频管道未初始化，无法处理IDR帧 (已收到 {Count} 个视频包)", _videoPacketCount);
                    _lastVideoPipelineWarningTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 优先发送视频包失败");
            }
        }
        
        public void OnVideoPacket(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    if (_videoPacketCount < 3 && packet != null && packet.Length == 1)
                    {
                        _logger.LogError("❌ 视频包异常：长度只有 1 字节");
                    }
                    return;
                }

                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);

                if (_peerConnection == null)
                {
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ OnVideoPacket: _peerConnection is null, 已收到 {Count} 个视频包", _videoPacketCount);
                    }
                    return;
                }

                // ✅ 使用新的模块化视频处理管道
                if (_videoPipeline != null)
                {
                    // ✅ 非阻塞异步发送
                    _ = _videoPipeline.OnNormalFrame(packet);
                    return;
                }
                
                // ⚠️ 如果管道未初始化，记录警告（限流：每 10 秒最多一次）
                var now = DateTime.UtcNow;
                if ((now - _lastVideoPipelineWarningTime).TotalSeconds >= VIDEO_PIPELINE_WARNING_INTERVAL_SECONDS)
                {
                    _logger.LogWarning("⚠️ 视频管道未初始化，无法处理普通帧 (已收到 {Count} 个视频包)", _videoPacketCount);
                    _lastVideoPipelineWarningTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送视频包失败: packetLen={Len}, count={Count}",
                    packet?.Length ?? 0, _videoPacketCount);
            }
        }

        // 旧的视频发送方法已移除，现在使用新的模块化 VideoPipeline

        private string? DetectCodecFromVideoHeader(byte[] header)
        {
            if (header == null || header.Length < 5)
            {
                return null;
            }

            int actualHeaderLen = header.Length >= 64 ? header.Length - 64 : header.Length;

            for (int i = 0; i < actualHeaderLen - 4; i++)
            {
                if (i + 4 < actualHeaderLen &&
                    header[i] == 0x00 && header[i + 1] == 0x00 &&
                    header[i + 2] == 0x00 && header[i + 3] == 0x01)
                {
                    byte nalType = header[i + 4];

                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }

                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }

                if (i + 3 < actualHeaderLen &&
                    header[i] == 0x00 && header[i + 1] == 0x00 && header[i + 2] == 0x01)
                {
                    byte nalType = header[i + 3];

                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }

                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }
            }

            return null;
        }
        
        private bool IsIdrFrame(byte[] buf, int hintOffset)
        {
            if (buf == null || buf.Length < 6) return false;

            bool AnnexBScan(int start)
            {
                for (int i = start; i <= buf.Length - 4; i++)
                {
                    if (buf[i] == 0x00 && buf[i + 1] == 0x00)
                    {
                        int nalStart = -1;
                        if (i + 3 < buf.Length && buf[i + 2] == 0x00 && buf[i + 3] == 0x01) nalStart = i + 4;
                        else if (buf[i + 2] == 0x01) nalStart = i + 3;
                        if (nalStart >= 0 && nalStart < buf.Length)
                        {
                            byte h = buf[nalStart];

                            int hevcType = (h >> 1) & 0x3F;
                            if (hevcType == 19 || hevcType == 20 || hevcType == 21 ||
                                hevcType == 16 || hevcType == 17 || hevcType == 18)
                            {
                                return true;
                            }

                            int h264Type = h & 0x1F;
                            if (h264Type == 5)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            return AnnexBScan(0) || (hintOffset > 0 && AnnexBScan(hintOffset));
        }
    }
}


