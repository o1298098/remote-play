using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Protocol;

namespace RemotePlay.Services.Streaming.Pipeline
{
    /// <summary>
    /// Output Pipeline - 负责异步发送帧到 WebRTC Receiver
    /// 设计目标：
    /// 1. 完全异步（不阻塞上游 Pipeline）
    /// 2. 优先级队列（IDR 关键帧优先发送）
    /// 3. 背压保护（队列满时丢弃旧帧）
    /// 4. 性能监控
    /// </summary>
    public sealed class OutputPipeline : IDisposable
    {
        private readonly ILogger<OutputPipeline> _logger;
        private volatile IAVReceiver _receiver;
        private readonly Channel<ProcessedFrame> _videoChannel;
        private readonly Channel<ProcessedFrame> _audioChannel;
        private readonly int _videoQueueCapacity;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _videoSendTask;
        private readonly Task _audioSendTask;

        // ✅ 帧率控制：目标60fps，即每帧间隔约16.67ms
        private const int TARGET_FPS = 60;
        private const double TARGET_FRAME_INTERVAL_MS = 1000.0 / TARGET_FPS; // ~16.67ms
        private DateTime _lastVideoFrameSentTime = DateTime.MinValue;

        // 统计
        private long _videoFramesSent;
        private long _audioFramesSent;
        private long _videoFramesDropped;
        private long _audioFramesDropped;
        private long _priorityFramesSent;

        public OutputPipeline(
            ILogger<OutputPipeline> logger,
            IAVReceiver receiver,
            int videoQueueCapacity = 256,
            int audioQueueCapacity = 512)
        {
            _logger = logger;
            _receiver = receiver;
            _videoQueueCapacity = videoQueueCapacity;

            // ✅ 优化：视频队列使用 DropOldest 策略，但优先保留关键帧
            // 当队列满时，优先丢弃非关键帧，保留关键帧
            _videoChannel = Channel.CreateBounded<ProcessedFrame>(new BoundedChannelOptions(videoQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            // 音频队列 - 容量更大，优先保证音频连续性
            _audioChannel = Channel.CreateBounded<ProcessedFrame>(new BoundedChannelOptions(audioQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            // 启动独立发送线程
            _videoSendTask = Task.Run(VideoSendLoop, _cts.Token);
            _audioSendTask = Task.Run(AudioSendLoop, _cts.Token);
        }

        #region Public API

        /// <summary>
        /// 推送视频帧（非阻塞）
        /// ✅ 优化：当队列满时，优先丢弃非关键帧，保留关键帧
        /// </summary>
        public bool TryPushVideoFrame(ProcessedFrame frame)
        {
            // ✅ 如果是关键帧且队列接近满，尝试先丢弃一个非关键帧
            if (frame.IsKeyFrame && _videoChannel.Reader.Count >= _videoQueueCapacity * 0.8)
            {
                // 尝试读取并丢弃一个非关键帧
                if (_videoChannel.Reader.TryRead(out ProcessedFrame oldFrame))
                {
                    if (!oldFrame.IsKeyFrame)
                    {
                        Interlocked.Increment(ref _videoFramesDropped);
                        _logger.LogDebug("🔍 Output video queue: dropped non-keyframe to make room for keyframe");
                    }
                    else
                    {
                        // 如果是关键帧，放回去
                        _videoChannel.Writer.TryWrite(oldFrame);
                    }
                }
            }
            
            bool success = _videoChannel.Writer.TryWrite(frame);
            if (!success)
            {
                Interlocked.Increment(ref _videoFramesDropped);
                // ✅ 如果是关键帧，记录警告；如果是普通帧，只记录调试信息
                if (frame.IsKeyFrame)
                {
                    _logger.LogWarning("⚠️ Output video queue full, dropping keyframe={Frame}", frame.FrameIndex);
                }
                else
                {
                    _logger.LogDebug("⚠️ Output video queue full, dropping frame={Frame}", frame.FrameIndex);
                }
            }
            return success;
        }

        /// <summary>
        /// 推送音频帧（非阻塞）
        /// </summary>
        public bool TryPushAudioFrame(ProcessedFrame frame)
        {
            bool success = _audioChannel.Writer.TryWrite(frame);
            if (!success)
            {
                Interlocked.Increment(ref _audioFramesDropped);
                _logger.LogWarning("⚠️ Output audio queue full, dropping frame={Frame}", frame.FrameIndex);
            }
            return success;
        }

        /// <summary>
        /// 设置接收器（支持动态切换，例如从 DefaultReceiver 切换到 WebRTCReceiver）
        /// </summary>
        public void SetReceiver(IAVReceiver receiver)
        {
            if (receiver == null)
            {
                _logger.LogWarning("⚠️ SetReceiver: receiver is null");
                return;
            }
            
            var oldReceiver = _receiver?.GetType().Name ?? "null";
            _receiver = receiver;
            _logger.LogInformation("✅ OutputPipeline: Receiver switched from {Old} to {New}", 
                oldReceiver, receiver.GetType().Name);
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public OutputStats GetStats()
        {
            return new OutputStats
            {
                VideoFramesSent = Interlocked.Read(ref _videoFramesSent),
                AudioFramesSent = Interlocked.Read(ref _audioFramesSent),
                VideoFramesDropped = Interlocked.Read(ref _videoFramesDropped),
                AudioFramesDropped = Interlocked.Read(ref _audioFramesDropped),
                PriorityFramesSent = Interlocked.Read(ref _priorityFramesSent),
                VideoQueueSize = _videoChannel.Reader.Count,
                AudioQueueSize = _audioChannel.Reader.Count
            };
        }

        #endregion

        #region Send Loops

        private async Task VideoSendLoop()
        {
            _logger.LogInformation("✅ OutputPipeline video sender started");

            try
            {
                long receivedCount = 0;
                _lastVideoFrameSentTime = DateTime.UtcNow;
                
                await foreach (var frame in _videoChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    receivedCount++;
                    try
                    {
                        // ✅ 游戏串流优化：优先低延迟，只在极端情况下才限制帧率
                        var now = DateTime.UtcNow;
                        if (_lastVideoFrameSentTime != DateTime.MinValue)
                        {
                            var elapsed = (now - _lastVideoFrameSentTime).TotalMilliseconds;
                            var queueSize = _videoChannel.Reader.Count;
                            
                            // ✅ 游戏串流：优先低延迟，只在发送过快时才轻微限制
                            // ✅ 关键帧和积压严重时：立即发送，不延迟
                            if (frame.IsKeyFrame || queueSize >= 20)
                            {
                                // 关键帧或队列积压：立即发送，保证低延迟
                                // 不延迟
                            }
                            else if (!frame.IsKeyFrame && queueSize < 20)
                            {
                                // ✅ 正常情况：只在发送过快（< 8ms，即>125fps）时才轻微延迟，避免极端情况
                                // 游戏串流允许更高的帧率波动，优先保证低延迟
                                var minInterval = 8.0; // 最小间隔8ms（最大125fps），只在极端情况下限制
                                if (elapsed < minInterval)
                                {
                                    var delayMs = minInterval - elapsed;
                                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _cts.Token);
                                    now = DateTime.UtcNow;
                                }
                            }
                        }
                        _lastVideoFrameSentTime = now;

                        // 构建带 header 的包
                        var packetData = new byte[1 + frame.Data.Length];
                        packetData[0] = (byte)HeaderType.VIDEO;
                        Array.Copy(frame.Data, 0, packetData, 1, frame.Data.Length);

                        // ⚠️ 调试：记录发送的帧信息（使用 Information 级别，确保不会被过滤）
                        if (receivedCount % 100 == 0 || frame.IsKeyFrame)
                        {
                            var queueSize = _videoChannel.Reader.Count;
                            _logger.LogDebug("🔍 OutputPipeline: Sending video frame={Frame}, isKeyFrame={Key}, dataLen={Len}, receiver={Receiver}, received={Received}, queueSize={Queue}",
                                frame.FrameIndex, frame.IsKeyFrame, packetData.Length, _receiver?.GetType().Name ?? "null", receivedCount, queueSize);
                        }

                        // 根据是否为关键帧选择发送方式
                        if (frame.IsKeyFrame && _receiver is WebRTCReceiver webrtcReceiver)
                        {
                            webrtcReceiver.OnVideoPacketPriority(packetData);
                            Interlocked.Increment(ref _priorityFramesSent);
                        }
                        else
                        {
                            _receiver.OnVideoPacket(packetData);
                        }

                        Interlocked.Increment(ref _videoFramesSent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Video send error, frame={Frame}", frame.FrameIndex);
                    }
                }
                _logger.LogDebug("🔍 OutputPipeline VideoSendLoop: Total received {Count} frames, sent {Sent} frames", 
                    receivedCount, Interlocked.Read(ref _videoFramesSent));
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ VideoSendLoop exception");
            }
            finally
            {
                _logger.LogInformation("✅ OutputPipeline video sender exited");
            }
        }

        private async Task AudioSendLoop()
        {
            _logger.LogInformation("✅ OutputPipeline audio sender started");

            try
            {
                await foreach (var frame in _audioChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        // 构建带 header 的包
                        var packetData = new byte[1 + frame.Data.Length];
                        packetData[0] = (byte)HeaderType.AUDIO;
                        Array.Copy(frame.Data, 0, packetData, 1, frame.Data.Length);

                        _receiver.OnAudioPacket(packetData);
                        Interlocked.Increment(ref _audioFramesSent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Audio send error, frame={Frame}", frame.FrameIndex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AudioSendLoop exception");
            }
            finally
            {
                _logger.LogInformation("✅ OutputPipeline audio sender exited");
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            _videoChannel.Writer.Complete();
            _audioChannel.Writer.Complete();
            _cts.Cancel();

            try
            {
                Task.WaitAll(new[] { _videoSendTask, _audioSendTask }, TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ OutputPipeline dispose error");
            }

            _cts.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Output Pipeline 统计信息
    /// </summary>
    public struct OutputStats
    {
        public long VideoFramesSent { get; set; }
        public long AudioFramesSent { get; set; }
        public long VideoFramesDropped { get; set; }
        public long AudioFramesDropped { get; set; }
        public long PriorityFramesSent { get; set; }
        public int VideoQueueSize { get; set; }
        public int AudioQueueSize { get; set; }
    }
}

