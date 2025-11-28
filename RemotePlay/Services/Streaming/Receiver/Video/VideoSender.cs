using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 视频发送器 - 完全异步实现
    /// 使用 Channel 实现非阻塞队列，后台任务处理发送
    /// </summary>
    internal class VideoSender : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly RTCPeerConnection? _peerConnection;
        private readonly MediaStreamTrack? _videoTrack;
        private readonly ConnectionStateMonitor _stateMonitor;
        private readonly TimestampManager _timestampManager;
        
        // 异步发送队列
        private readonly Channel<VideoFrame> _sendChannel;
        private readonly ChannelWriter<VideoFrame> _sendWriter;
        private readonly ChannelReader<VideoFrame> _sendReader;
        private readonly Task _sendTask;
        private readonly CancellationTokenSource _cts = new();
        
        // 发送统计
        private int _sentCount = 0;
        private int _failedCount = 0;
        
        // 反射方法缓存（需要从外部传入或自己初始化）
        private System.Reflection.MethodInfo? _cachedSendVideoMethod;
        private bool _methodsInitialized = false;
        
        // 视频格式
        private string _detectedVideoFormat = "h264";
        
        public VideoSender(
            ILogger? logger,
            RTCPeerConnection? peerConnection,
            MediaStreamTrack? videoTrack,
            ConnectionStateMonitor stateMonitor,
            TimestampManager timestampManager)
        {
            _logger = logger;
            _peerConnection = peerConnection;
            _videoTrack = videoTrack;
            _stateMonitor = stateMonitor;
            _timestampManager = timestampManager;
            
            // ✅ 创建有界Channel，防止内存无限增长
            var options = new BoundedChannelOptions(20)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // 队列满时丢弃最旧的
                SingleReader = true,  // 单消费者
                SingleWriter = false  // 多生产者
            };
            
            _sendChannel = Channel.CreateBounded<VideoFrame>(options);
            _sendWriter = _sendChannel.Writer;
            _sendReader = _sendChannel.Reader;
            
            // ✅ 启动后台发送任务
            _sendTask = Task.Run(ProcessSendQueueAsync, _cts.Token);
        }
        
        /// <summary>
        /// 异步发送视频帧（非阻塞）
        /// </summary>
        public async ValueTask<bool> SendAsync(VideoFrame frame)
        {
            if (frame == null) return false;
            
            try
            {
                // ✅ 非阻塞写入
                await _sendWriter.WriteAsync(frame, _cts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// 后台处理发送队列（完全异步）
        /// </summary>
        private async Task ProcessSendQueueAsync()
        {
            try
            {
                await foreach (var frame in _sendReader.ReadAllAsync(_cts.Token))
                {
                    // ✅ 优化：在处理每个帧前检查取消信号
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    
                    try
                    {
                        // ✅ 获取时间戳（线程安全）
                        uint timestamp = _timestampManager.GetNextTimestamp(frame.Timestamp);
                        frame.RtpTimestamp = timestamp;
                        
                        // ✅ 异步发送
                        bool sent = await TrySendFrameAsync(frame);
                        
                        if (sent)
                        {
                            Interlocked.Increment(ref _sentCount);
                        }
                        else
                        {
                            Interlocked.Increment(ref _failedCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedCount);
                        _logger?.LogWarning(ex, "⚠️ 处理视频帧失败: FrameIndex={Index}", frame.FrameIndex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ 发送队列处理异常");
            }
        }
        
        /// <summary>
        /// 异步尝试发送帧
        /// </summary>
        private async Task<bool> TrySendFrameAsync(VideoFrame frame)
        {
            if (_peerConnection == null || _videoTrack == null || frame.Data == null || frame.Data.Length == 0)
            {
                return false;
            }
            
            // ✅ 检查连接状态（使用缓存）
            if (!_stateMonitor.CanSendVideo())
            {
                return false;
            }
            
            // ✅ 初始化反射方法（如果需要）
            if (!_methodsInitialized)
            {
                InitializeReflectionMethods();
            }
            
            // ✅ 尝试直接发送
            if (_cachedSendVideoMethod != null)
            {
                try
                {
                    bool sent = await SafeInvokeMethodAsync(
                        () => _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { frame.RtpTimestamp, frame.Data }),
                        "SendVideo",
                        100);
                    
                    if (sent)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "⚠️ SendVideo 直接发送失败");
                    _cachedSendVideoMethod = null;
                    _methodsInitialized = false;
                }
            }
            
            // ✅ 如果直接发送失败，尝试RTP方式（需要实现）
            // 这里可以调用原有的 SendVideoRTP 逻辑，但需要改为异步
            return false;
        }
        
        /// <summary>
        /// 异步安全调用方法（带超时）
        /// </summary>
        private async Task<bool> SafeInvokeMethodAsync(Action invokeAction, string methodName, int timeoutMs = 100)
        {
            try
            {
                var cts = new CancellationTokenSource(timeoutMs);
                
                // ✅ 使用 Task.Run 在后台线程执行，避免阻塞
                var task = Task.Run(() =>
                {
                    try
                    {
                        invokeAction();
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"{methodName} failed", ex);
                    }
                }, cts.Token);
                
                await task;
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogWarning("⚠️ {Method} 调用超时（{Timeout}ms）", methodName, timeoutMs);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "⚠️ {Method} 调用失败", methodName);
                return false;
            }
        }
        
        /// <summary>
        /// 初始化反射方法
        /// </summary>
        private void InitializeReflectionMethods()
        {
            if (_peerConnection == null) return;
            
            try
            {
                var peerConnectionType = _peerConnection.GetType();
                var sendVideoMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name == "SendVideo")
                    .ToList();
                
                foreach (var method in sendVideoMethods)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(uint) &&
                        parameters[1].ParameterType == typeof(byte[]))
                    {
                        _cachedSendVideoMethod = method;
                        _methodsInitialized = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "⚠️ 初始化反射方法失败");
            }
        }
        
        /// <summary>
        /// 发送统计
        /// </summary>
        public (int sent, int failed) GetStats()
        {
            return (_sentCount, _failedCount);
        }
        
        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _sendWriter.Complete();
                
                // ✅ 优化：使用更短的等待时间，避免阻塞主线程
                try
                {
                    _sendTask.Wait(TimeSpan.FromMilliseconds(500)); // 减少等待时间到 500ms
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "VideoSender 等待发送任务退出时异常");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "⚠️ VideoSender Dispose 异常");
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}

