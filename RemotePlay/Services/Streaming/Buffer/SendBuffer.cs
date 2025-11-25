using System.Collections.Concurrent;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// 发送缓冲区 - 优化包发送性能
    /// 
    /// 功能：
    /// 1. 批量发送多个小包
    /// 2. 避免频繁的系统调用
    /// 3. 提供流控（flow control）
    /// </summary>
    public class SendBuffer : IDisposable
    {
        #region Constants
        
        private const int DEFAULT_CAPACITY = 256;          // 默认缓冲区容量
        private const int DEFAULT_FLUSH_INTERVAL_MS = 1;   // 默认刷新间隔（毫秒）
        private const int MAX_BATCH_SIZE = 32;             // 最大批量发送数
        
        #endregion
        
        #region Fields
        
        private readonly ILogger _logger;
        private readonly Func<byte[], Task> _sendFunc;     // 实际发送函数
        
        private readonly ConcurrentQueue<QueuedPacket> _queue;
        private readonly SemaphoreSlim _semaphore;
        
        private CancellationTokenSource? _cts;
        private Task? _flushTask;
        
        private readonly int _capacity;
        private readonly int _flushIntervalMs;
        
        private bool _isRunning = false;
        
        // 统计信息
        private long _totalQueued = 0;
        private long _totalSent = 0;
        private long _totalDropped = 0;
        private long _totalBytes = 0;
        
        private readonly object _statsLock = new object();
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// 创建发送缓冲区
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="sendFunc">实际发送函数</param>
        /// <param name="capacity">缓冲区容量</param>
        /// <param name="flushIntervalMs">刷新间隔（毫秒）</param>
        public SendBuffer(
            ILogger logger,
            Func<byte[], Task> sendFunc,
            int capacity = DEFAULT_CAPACITY,
            int flushIntervalMs = DEFAULT_FLUSH_INTERVAL_MS)
        {
            _logger = logger;
            _sendFunc = sendFunc;
            _capacity = capacity;
            _flushIntervalMs = flushIntervalMs;
            
            _queue = new ConcurrentQueue<QueuedPacket>();
            _semaphore = new SemaphoreSlim(0);
        }
        
        #endregion
        
        #region Lifecycle
        
        /// <summary>
        /// 启动发送缓冲区
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                _logger.LogWarning("SendBuffer already running");
                return;
            }
            
            _cts = new CancellationTokenSource();
            _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token), _cts.Token);
            _isRunning = true;
            
            _logger.LogInformation("✅ SendBuffer started: capacity={Capacity}, flush_interval={Interval}ms",
                _capacity, _flushIntervalMs);
        }
        
        /// <summary>
        /// 停止发送缓冲区
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;
            
            // 先刷新所有待发送的包
            await FlushAsync();
            
            _cts?.Cancel();
            
            if (_flushTask != null)
            {
                try
                {
                    await _flushTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            
            _isRunning = false;
            
            _logger.LogInformation("SendBuffer stopped: queued={Queued}, sent={Sent}, dropped={Dropped}, bytes={Bytes}",
                _totalQueued, _totalSent, _totalDropped, _totalBytes);
        }
        
        public void Dispose()
        {
            // ✅ 关键修复：使用超时机制，避免 Dispose 阻塞太久
            try
            {
                var stopTask = StopAsync();
                var timeoutTask = Task.Delay(1000); // 最多等待 1 秒
                var completedTask = Task.WhenAny(stopTask, timeoutTask).GetAwaiter().GetResult();
                
                if (completedTask == timeoutTask)
                {
                    // 注意：SendBuffer 可能没有 logger，所以不记录日志
                }
            }
            catch (Exception)
            {
                // 忽略异常，继续释放
            }
            
            _cts?.Dispose();
            _semaphore.Dispose();
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// 将包加入发送队列
        /// </summary>
        /// <param name="packet">要发送的包</param>
        /// <param name="priority">优先级（高优先级的包会被立即发送）</param>
        /// <returns>是否成功加入队列</returns>
        public bool Enqueue(byte[] packet, SendPriority priority = SendPriority.Normal)
        {
            if (!_isRunning)
            {
                _logger.LogWarning("SendBuffer not running, dropping packet");
                Interlocked.Increment(ref _totalDropped);
                return false;
            }
            
            // 检查队列大小
            if (_queue.Count >= _capacity)
            {
                _logger.LogWarning("SendBuffer full (size={Size}), dropping packet", _queue.Count);
                Interlocked.Increment(ref _totalDropped);
                return false;
            }
            
            var queuedPacket = new QueuedPacket
            {
                Data = packet,
                Priority = priority,
                QueueTime = DateTime.UtcNow
            };
            
            _queue.Enqueue(queuedPacket);
            Interlocked.Increment(ref _totalQueued);
            
            // 如果是高优先级，立即通知刷新
            if (priority == SendPriority.High)
            {
                _semaphore.Release();
            }
            
            return true;
        }
        
        /// <summary>
        /// 立即发送包（不经过缓冲区）
        /// </summary>
        public async Task SendImmediateAsync(byte[] packet)
        {
            await _sendFunc(packet);
            
            Interlocked.Increment(ref _totalSent);
            Interlocked.Add(ref _totalBytes, packet.Length);
        }
        
        /// <summary>
        /// 刷新缓冲区（发送所有待发送的包）
        /// </summary>
        public async Task FlushAsync()
        {
            var batch = new List<QueuedPacket>();
            
            // 取出所有待发送的包
            while (_queue.TryDequeue(out var packet))
            {
                batch.Add(packet);
            }
            
            // 批量发送
            if (batch.Count > 0)
            {
                await SendBatchAsync(batch);
            }
        }
        
        /// <summary>
        /// 获取统计信息
        /// </summary>
        public (long queued, long sent, long dropped, long bytes, int pending) GetStats()
        {
            lock (_statsLock)
            {
                return (_totalQueued, _totalSent, _totalDropped, _totalBytes, _queue.Count);
            }
        }
        
        #endregion
        
        #region Private Methods
        
        /// <summary>
        /// 刷新循环
        /// </summary>
        private async Task FlushLoopAsync(CancellationToken ct)
        {
            _logger.LogDebug("SendBuffer flush loop started");
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 等待一段时间或有新的高优先级包
                    await Task.WhenAny(
                        Task.Delay(_flushIntervalMs, ct),
                        _semaphore.WaitAsync(ct)
                    );
                    
                    // 批量发送
                    await FlushBatchAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SendBuffer flush loop");
                    await Task.Delay(10, ct);
                }
            }
            
            _logger.LogDebug("SendBuffer flush loop exited");
        }
        
        /// <summary>
        /// 刷新一批包
        /// </summary>
        private async Task FlushBatchAsync(CancellationToken ct)
        {
            var batch = new List<QueuedPacket>();
            
            // 取出一批包（最多 MAX_BATCH_SIZE 个）
            for (int i = 0; i < MAX_BATCH_SIZE && _queue.TryDequeue(out var packet); i++)
            {
                batch.Add(packet);
            }
            
            if (batch.Count > 0)
            {
                await SendBatchAsync(batch);
            }
        }
        
        /// <summary>
        /// 批量发送包
        /// </summary>
        private async Task SendBatchAsync(List<QueuedPacket> batch)
        {
            // 按优先级排序（高优先级优先发送）
            batch.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            
            // 逐个发送
            foreach (var packet in batch)
            {
                try
                {
                    await _sendFunc(packet.Data);
                    
                    Interlocked.Increment(ref _totalSent);
                    Interlocked.Add(ref _totalBytes, packet.Data.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send packet");
                    Interlocked.Increment(ref _totalDropped);
                }
            }
            
            // 定期日志
            if (_totalSent % 1000 == 0)
            {
                _logger.LogTrace("SendBuffer: sent={Sent}, pending={Pending}",
                    _totalSent, _queue.Count);
            }
        }
        
        #endregion
        
        #region Inner Types
        
        /// <summary>
        /// 排队的包
        /// </summary>
        private class QueuedPacket
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public SendPriority Priority { get; set; }
            public DateTime QueueTime { get; set; }
        }
        
        #endregion
    }
    
    /// <summary>
    /// 发送优先级
    /// </summary>
    public enum SendPriority
    {
        Low = 0,        // 低优先级（可以延迟发送）
        Normal = 1,     // 正常优先级
        High = 2        // 高优先级（立即发送）
    }
}

