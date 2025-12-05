using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 视频处理管道 - 整合所有模块，提供统一接口
    /// 符合主流设计：生产者-消费者模式、异步处理、无锁队列
    /// </summary>
    internal class VideoPipeline : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly RTCPeerConnection? _peerConnection;
        private readonly MediaStreamTrack? _videoTrack;
        
        private readonly VideoQueueManager _queueManager;
        private readonly TimestampManager _timestampManager;
        private readonly ConnectionStateMonitor _stateMonitor;
        private readonly Channel<VideoFrame> _processingChannel;
        private readonly ChannelWriter<VideoFrame> _processingWriter;
        private readonly ChannelReader<VideoFrame> _processingReader;
        private readonly Task _processingTask;
        private readonly CancellationTokenSource _cts = new();
        private readonly RtpPacketizer _rtpPacketizer;
        private readonly ReflectionMethodCache _methodCache;
        
        private long _currentFrameIndex = 0;
        private int _sentCount = 0;
        private int _failedCount = 0;
        private DateTime _lastStatsLogTime = DateTime.MinValue;
        private const int STATS_LOG_INTERVAL_MS = 5000;
        private uint _videoSsrc;
        private int _negotiatedPtH264 = 96;
        private int _negotiatedPtHevc = 97;
        private string _detectedVideoFormat = "h264";
        private Action<long>? _onPacketSent;
        
        private DateTime _lastKeyframeRequestTime = DateTime.MinValue;
        private const int KEYFRAME_REQUEST_COOLDOWN_MS = 2000;
        private Action? _onKeyframeRequest;
        
        public VideoPipeline(
            ILogger? logger,
            RTCPeerConnection? peerConnection,
            MediaStreamTrack? videoTrack,
            uint videoSsrc = 0,
            string detectedVideoFormat = "h264",
            int negotiatedPtH264 = 96,
            int negotiatedPtHevc = 97)
        {
            _logger = logger;
            _peerConnection = peerConnection;
            _videoTrack = videoTrack;
            _videoSsrc = videoSsrc;
            _detectedVideoFormat = detectedVideoFormat;
            _negotiatedPtH264 = negotiatedPtH264;
            _negotiatedPtHevc = negotiatedPtHevc;
            
            _queueManager = new VideoQueueManager();
            _timestampManager = new TimestampManager();
            _stateMonitor = new ConnectionStateMonitor(peerConnection);
            
            _methodCache = new ReflectionMethodCache(logger, peerConnection, videoTrack);
            _methodCache.Initialize();
            
            _rtpPacketizer = new RtpPacketizer(logger, _methodCache, _detectedVideoFormat, _negotiatedPtH264, _negotiatedPtHevc);
            
            // 低延迟优化：40 帧队列 (≈ 0.67s @ 60fps)
            var options = new BoundedChannelOptions(40)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            
            _processingChannel = Channel.CreateBounded<VideoFrame>(options);
            _processingWriter = _processingChannel.Writer;
            _processingReader = _processingChannel.Reader;
            _processingTask = Task.Run(ProcessQueueAsync, _cts.Token);
        }
        
        /// <summary>
        /// 设置统计回调
        /// </summary>
        public void SetOnPacketSent(Action<long> callback)
        {
            _onPacketSent = callback;
        }
        
        /// <summary>
        /// 设置关键帧请求回调（背压机制）
        /// </summary>
        public void SetOnKeyframeRequest(Action callback)
        {
            _onKeyframeRequest = callback;
        }
        
        /// <summary>
        /// 处理IDR关键帧
        /// </summary>
        public ValueTask<bool> OnIdrFrame(byte[] packet)
        {
            if (packet == null || packet.Length <= 1)
            {
                return ValueTask.FromResult(false);
            }
            
            try
            {
                int currentQueueSize = _queueManager.TotalCount;
                if (currentQueueSize > 30)
                {
                    _logger?.LogWarning("⚠️ 视频队列积压 ({Queue}/40)，可能出现发送瓶颈", currentQueueSize);
                }
                
                _currentFrameIndex++;
                
                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);
                
                var frame = new VideoFrame(
                    videoData,
                    isIdr: true,
                    frameIndex: _currentFrameIndex,
                    timestamp: DateTime.UtcNow);
                
                bool written = _processingWriter.TryWrite(frame);
                return ValueTask.FromResult(written);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OnIdrFrame 异常");
                return ValueTask.FromResult(false);
            }
        }
        
        /// <summary>
        /// 处理普通帧（低延迟+连续画面优化）
        /// </summary>
        public ValueTask<bool> OnNormalFrame(byte[] packet)
        {
            if (packet == null || packet.Length <= 1)
            {
                return ValueTask.FromResult(false);
            }
            
            try
            {
                int currentQueueSize = _queueManager.TotalCount;
                
                // 丢帧策略：队列 > 35 帧时触发
                if (currentQueueSize > 35)
                {
                    int totalAttempts = _sentCount + _failedCount;
                    double failureRate = totalAttempts > 0 ? (double)_failedCount / totalAttempts : 0;
                    
                    // 队列接近满或失败率高：立即丢帧
                    if (currentQueueSize >= 38 || failureRate > 0.5)
                    {
                        if (_sentCount % 60 == 0)
                        {
                            _logger?.LogWarning("⚠️ 视频队列接近满 ({Queue}/40)，失败率 {Rate:P1}，丢弃普通帧", 
                                currentQueueSize, failureRate);
                        }
                        return ValueTask.FromResult(false);
                    }
                    
                    // 中度积压：渐进式概率丢帧
                    double dropProbability = (currentQueueSize - 35) / 6.0;
                    if (Random.Shared.Next(100) < dropProbability * 100)
                    {
                        return ValueTask.FromResult(false);
                    }
                }
                
                _currentFrameIndex++;
                
                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);
                
                var frame = new VideoFrame(
                    videoData,
                    isIdr: false,
                    frameIndex: _currentFrameIndex,
                    timestamp: DateTime.UtcNow);
                
                bool written = _processingWriter.TryWrite(frame);
                return ValueTask.FromResult(written);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OnNormalFrame 异常");
                return ValueTask.FromResult(false);
            }
        }
        
        /// <summary>
        /// 后台处理队列（完全异步，无阻塞）
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            var batch = new List<VideoFrame>(10);
            
            try
            {
                await foreach (var frame in _processingReader.ReadAllAsync(_cts.Token))
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    
                    try
                    {
                        if (frame.IsIdr)
                        {
                            _queueManager.ClearOldFrames(framesToKeep: 5);
                            _queueManager.TryEnqueueIdr(frame);
                        }
                        else
                        {
                            _queueManager.TryEnqueueNormal(frame);
                        }
                        
                        // 背压机制：队列 > 30 帧时请求关键帧
                        int currentQueueSize = _queueManager.TotalCount;
                        if (currentQueueSize > 30 && _onKeyframeRequest != null)
                        {
                            var backpressureCheckTime = DateTime.UtcNow;
                            var timeSinceLastRequest = (backpressureCheckTime - _lastKeyframeRequestTime).TotalMilliseconds;
                            
                            if (timeSinceLastRequest >= KEYFRAME_REQUEST_COOLDOWN_MS)
                            {
                                _lastKeyframeRequestTime = backpressureCheckTime;
                                _logger?.LogWarning("🔄 队列积压严重 ({Queue}/100)，触发背压机制，请求关键帧以重新同步", currentQueueSize);
                                
                                // 清空大部分旧帧，保留最近的几帧
                                int cleared = _queueManager.ClearOldFrames(framesToKeep: 10);
                                if (cleared > 0)
                                {
                                    _logger?.LogInformation("🧹 已清空 {Cleared} 帧旧数据，等待新的关键帧", cleared);
                                }
                                
                                // 触发关键帧请求
                                try
                                {
                                    _onKeyframeRequest.Invoke();
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning(ex, "触发关键帧请求回调时出错");
                                }
                            }
                        }
                        
                        // 批量处理：动态调整批量大小
                        int queueSize = _queueManager.TotalCount;
                        int maxBatchSize;
                        if (queueSize > 30)
                        {
                            maxBatchSize = 10;
                        }
                        else if (queueSize > 15)
                        {
                            maxBatchSize = 5;
                        }
                        else
                        {
                            maxBatchSize = 3;
                        }
                        int dequeued = _queueManager.TryDequeueBatch(batch, maxCount: maxBatchSize);
                        
                        for (int i = 0; i < dequeued; i++)
                        {
                            if (_cts.Token.IsCancellationRequested)
                            {
                                break;
                            }
                            
                            var frameToSend = batch[i];
                            uint timestamp = _timestampManager.GetNextTimestamp(frameToSend.Timestamp);
                            frameToSend.RtpTimestamp = timestamp;
                            
                            // 动态超时策略
                            int dynamicTimeout = 100;
                            int dynamicRetries = 1;
                            
                            int totalAttempts = _sentCount + _failedCount;
                            if (totalAttempts > 100)
                            {
                                double failureRate = (double)_failedCount / totalAttempts;
                                if (failureRate > 0.3)
                                {
                                    dynamicTimeout = 200;
                                    dynamicRetries = 2;
                                }
                                else if (failureRate > 0.1)
                                {
                                    dynamicTimeout = 150;
                                    dynamicRetries = 2;
                                }
                            }
                            
                            bool sent = await TrySendFrameAsync(frameToSend, dynamicTimeout, dynamicRetries);
                            
                            if (sent)
                            {
                                Interlocked.Increment(ref _sentCount);
                                _onPacketSent?.Invoke(frameToSend.FrameIndex);
                            }
                            else
                            {
                                Interlocked.Increment(ref _failedCount);
                                int failed = _failedCount;
                                int sentCount = _sentCount;
                                
                                if (failed > 5 && sentCount == 0)
                                {
                                    _logger?.LogError("连续发送失败 {Failed} 次，可能连接已断开，尝试请求关键帧", failed);
                                }
                                else if (failed > 0 && (failed % 50 == 0))
                                {
                                    double failureRate = sentCount > 0 ? (double)failed / (failed + sentCount) : 1.0;
                                    if (failureRate > 0.5)
                                    {
                                        _logger?.LogWarning("视频发送失败率高: {Failed}/{Total} ({Rate:P1}), 当前超时={Timeout}ms, 重试={Retries}次", 
                                            failed, failed + sentCount, failureRate, dynamicTimeout, dynamicRetries);
                                    }
                                }
                            }
                        }
                        
                        batch.Clear();
                        
                        var now = DateTime.UtcNow;
                        if (_lastStatsLogTime == DateTime.MinValue || 
                            (now - _lastStatsLogTime).TotalMilliseconds >= STATS_LOG_INTERVAL_MS)
                        {
                            int sent = _sentCount;
                            int failed = _failedCount;
                            int statsQueueSize = _queueManager.TotalCount;
                            
                            if (statsQueueSize > 10 || (failed > 0 && (double)failed / (sent + failed) > 0.1))
                            {
                                double failureRate = (sent + failed) > 0 ? (double)failed / (sent + failed) : 0;
                                _logger?.LogInformation("视频管道统计: 队列={Queue}, 已发送={Sent}, 失败={Failed}, 失败率={Rate:P1}", 
                                    statsQueueSize, sent, failed, failureRate);
                            }
                            
                            _lastStatsLogTime = now;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "处理视频帧失败: FrameIndex={Index}", frame.FrameIndex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "处理队列异常");
            }
        }
        
        /// <summary>
        /// 异步发送帧（完全异步，使用模块化组件，带重试和降级）
        /// </summary>
        private async Task<bool> TrySendFrameAsync(VideoFrame frame, int timeoutMs = 500, int maxRetries = 3)
        {
            if (_peerConnection == null || _videoTrack == null || frame.Data == null || frame.Data.Length == 0)
            {
                return false;
            }
            
            if (!_stateMonitor.CanSendVideo())
            {
                var (connectionState, _, _) = _stateMonitor.GetCachedState();
                if (connectionState == RTCPeerConnectionState.closed || 
                    connectionState == RTCPeerConnectionState.failed)
                {
                    return false;
                }
            }
            
            try
            {
                bool sent = await _methodCache.InvokeSendVideoAsync(frame.RtpTimestamp, frame.Data, timeoutMs, maxRetries);
                if (sent) return true;
                
                return await _rtpPacketizer.SendVideoDataAsync(frame.Data, frame.RtpTimestamp, _videoSsrc);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "TrySendFrameAsync 异常: FrameIndex={Index}", frame.FrameIndex);
                return false;
            }
        }
        
        
        /// <summary>
        /// 获取统计信息
        /// </summary>
        public (int sent, int failed, int queueSize) GetStats()
        {
            return (_sentCount, _failedCount, _queueManager.TotalCount);
        }
        
        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _processingWriter.Complete();
                
                try
                {
                    _processingTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "VideoPipeline 等待处理任务退出时异常");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "VideoPipeline Dispose 异常");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}

