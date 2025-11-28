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
            
            var options = new BoundedChannelOptions(20)
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
        /// 处理IDR帧（非阻塞）
        /// </summary>
        public async ValueTask<bool> OnIdrFrame(byte[] packet)
        {
            if (packet == null || packet.Length <= 1)
            {
                return false;
            }
            
            try
            {
                _currentFrameIndex++;
                
                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);
                
                var frame = new VideoFrame(
                    videoData,
                    isIdr: true,
                    frameIndex: _currentFrameIndex,
                    timestamp: DateTime.UtcNow);
                
                await _processingWriter.WriteAsync(frame, _cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OnIdrFrame 异常");
                return false;
            }
        }
        
        /// <summary>
        /// 处理普通帧（非阻塞）
        /// </summary>
        public async ValueTask<bool> OnNormalFrame(byte[] packet)
        {
            if (packet == null || packet.Length <= 1)
            {
                return false;
            }
            
            try
            {
                _currentFrameIndex++;
                
                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);
                
                var frame = new VideoFrame(
                    videoData,
                    isIdr: false,
                    frameIndex: _currentFrameIndex,
                    timestamp: DateTime.UtcNow);
                
                await _processingWriter.WriteAsync(frame, _cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "OnNormalFrame 异常");
                return false;
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
                // ✅ 优化：使用 CancellationToken 组合，确保能及时响应取消
                await foreach (var frame in _processingReader.ReadAllAsync(_cts.Token))
                {
                    // ✅ 优化：在处理每个帧前检查取消信号，避免处理已取消的帧
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
                        
                        int queueSize = _queueManager.TotalCount;
                        int maxBatchSize = queueSize > 10 ? Math.Min(queueSize / 2, 10) : 5;
                        int dequeued = _queueManager.TryDequeueBatch(batch, maxCount: maxBatchSize);
                        
                        for (int i = 0; i < dequeued; i++)
                        {
                            // ✅ 优化：在发送前检查取消信号
                            if (_cts.Token.IsCancellationRequested)
                            {
                                break;
                            }
                            
                            var frameToSend = batch[i];
                            uint timestamp = _timestampManager.GetNextTimestamp(frameToSend.Timestamp);
                            frameToSend.RtpTimestamp = timestamp;
                            
                            bool sent = await TrySendFrameAsync(frameToSend);
                            
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
                                
                                // ✅ 优化：降低连续失败阈值，更快检测问题
                                if (failed > 5 && sentCount == 0)
                                {
                                    _logger?.LogError("连续发送失败 {Failed} 次，可能连接已断开，尝试请求关键帧", failed);
                                    // 可以在这里添加请求关键帧的逻辑
                                }
                                else if (failed > 0 && (failed % 50 == 0))
                                {
                                    double failureRate = sentCount > 0 ? (double)failed / (failed + sentCount) : 1.0;
                                    if (failureRate > 0.5)
                                    {
                                        _logger?.LogWarning("视频发送失败率高: {Failed}/{Total} ({Rate:P1})", 
                                            failed, failed + sentCount, failureRate);
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
                            int currentQueueSize = _queueManager.TotalCount;
                            
                            if (currentQueueSize > 10 || (failed > 0 && (double)failed / (sent + failed) > 0.1))
                            {
                                double failureRate = (sent + failed) > 0 ? (double)failed / (sent + failed) : 0;
                                _logger?.LogInformation("视频管道统计: 队列={Queue}, 已发送={Sent}, 失败={Failed}, 失败率={Rate:P1}", 
                                    currentQueueSize, sent, failed, failureRate);
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
        private async Task<bool> TrySendFrameAsync(VideoFrame frame)
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
                bool sent = await _methodCache.InvokeSendVideoAsync(frame.RtpTimestamp, frame.Data, timeoutMs: 200, maxRetries: 2);
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
                
                // ✅ 优化：使用异步等待，避免阻塞主线程
                try
                {
                    _processingTask.Wait(TimeSpan.FromMilliseconds(500)); // 减少等待时间到 500ms
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

