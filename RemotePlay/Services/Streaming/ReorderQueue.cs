using System.Collections.Generic;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 包重排序队列 - 处理乱序到达的 AV 包
    /// 
    /// 工作原理：
    /// 1. 缓存乱序到达的包
    /// 2. 按序列号排序
    /// 3. 当缺失的包到达或超时后，按顺序输出包
    /// </summary>
    public class ReorderQueue<T> where T : class
    {
        #region Constants
        
        // ✅ 配置常量
        private const int DEFAULT_SIZE_MIN = 3;        // 最小队列大小
        private const int DEFAULT_SIZE_MAX = 32;       // 最大队列大小
        private const int DEFAULT_SIZE_START = 12;     // 初始队列大小
        private const int DEFAULT_TIMEOUT_MS = 50;     // 超时时间（毫秒）
        
        #endregion
        
        #region Fields
        
        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeqNum;     // 获取序列号的函数
        private readonly Action<T> _outputCallback;     // 输出回调
        
        private readonly SortedDictionary<uint, QueueEntry> _buffer;  // 缓冲区（按序列号排序）
        private readonly object _lock = new object();
        
        private uint _nextExpectedSeq = 0;              // 期望的下一个序列号
        private int _currentSize;                       // 当前队列大小
        private readonly int _sizeMin;                  // 最小队列大小
        private readonly int _sizeMax;                  // 最大队列大小
        private readonly int _timeoutMs;                // 超时时间
        
        private bool _initialized = false;              // 是否已初始化
        
        // 统计信息
        private ulong _totalProcessed = 0;
        private ulong _totalDropped = 0;
        private ulong _totalReordered = 0;
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// 创建重排序队列
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="getSeqNum">获取包序列号的函数</param>
        /// <param name="outputCallback">输出回调函数</param>
        /// <param name="sizeStart">初始队列大小</param>
        /// <param name="sizeMin">最小队列大小</param>
        /// <param name="sizeMax">最大队列大小</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            int sizeStart = DEFAULT_SIZE_START,
            int sizeMin = DEFAULT_SIZE_MIN,
            int sizeMax = DEFAULT_SIZE_MAX,
            int timeoutMs = DEFAULT_TIMEOUT_MS)
        {
            _logger = logger;
            _getSeqNum = getSeqNum;
            _outputCallback = outputCallback;
            
            _buffer = new SortedDictionary<uint, QueueEntry>();
            
            _currentSize = Math.Clamp(sizeStart, sizeMin, sizeMax);
            _sizeMin = sizeMin;
            _sizeMax = sizeMax;
            _timeoutMs = timeoutMs;
            
            _logger.LogDebug("ReorderQueue created: size={Size} (min={Min}, max={Max}), timeout={Timeout}ms",
                _currentSize, _sizeMin, _sizeMax, _timeoutMs);
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// 推入一个包
        /// </summary>
        public void Push(T item)
        {
            lock (_lock)
            {
                uint seqNum = _getSeqNum(item);
                
                // 首次初始化
                if (!_initialized)
                {
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    _logger.LogDebug("ReorderQueue initialized with seq={Seq}", seqNum);
                }
                
                // 检查序列号
                if (IsSeqBefore(seqNum, _nextExpectedSeq))
                {
                    // 包已过期，丢弃
                    _totalDropped++;
                    _logger.LogTrace("Dropped late packet: seq={Seq}, expected={Expected}", 
                        seqNum, _nextExpectedSeq);
                    return;
                }
                
                if (seqNum == _nextExpectedSeq)
                {
                    // 正好是期望的包，直接输出
                    _outputCallback(item);
                    _nextExpectedSeq++;
                    _totalProcessed++;
                    
                    // 尝试输出缓冲区中的后续包
                    FlushReady();
                }
                else
                {
                    // 乱序包，加入缓冲区
                    if (!_buffer.ContainsKey(seqNum))
                    {
                        var entry = new QueueEntry
                        {
                            Item = item,
                            ArrivalTime = DateTime.UtcNow
                        };
                        
                        _buffer[seqNum] = entry;
                        _totalReordered++;
                        
                        _logger.LogTrace("Buffered out-of-order packet: seq={Seq}, expected={Expected}, buffer_size={Size}",
                            seqNum, _nextExpectedSeq, _buffer.Count);
                        
                        // 检查缓冲区大小
                        CheckBufferSize();
                        
                        // 检查超时
                        CheckTimeout();
                    }
                    else
                    {
                        // 重复包，丢弃
                        _totalDropped++;
                    }
                }
            }
        }
        
        /// <summary>
        /// 刷新队列（输出所有超时的包）
        /// </summary>
        public void Flush(bool force = false)
        {
            lock (_lock)
            {
                if (force)
                {
                    // 强制输出所有缓冲的包
                    foreach (var kvp in _buffer)
                    {
                        _outputCallback(kvp.Value.Item);
                        _totalProcessed++;
                    }
                    _buffer.Clear();
                    
                    _logger.LogInformation("ReorderQueue force flushed: total={Total}, dropped={Dropped}, reordered={Reordered}",
                        _totalProcessed, _totalDropped, _totalReordered);
                }
                else
                {
                    CheckTimeout();
                }
            }
        }
        
        /// <summary>
        /// 获取统计信息
        /// </summary>
        public (ulong processed, ulong dropped, ulong reordered, int bufferSize) GetStats()
        {
            lock (_lock)
            {
                return (_totalProcessed, _totalDropped, _totalReordered, _buffer.Count);
            }
        }
        
        #endregion
        
        #region Private Methods
        
        /// <summary>
        /// 输出缓冲区中已就绪的包
        /// </summary>
        private void FlushReady()
        {
            while (_buffer.Count > 0)
            {
                // 检查下一个期望的包是否在缓冲区中
                if (_buffer.TryGetValue(_nextExpectedSeq, out var entry))
                {
                    _outputCallback(entry.Item);
                    _buffer.Remove(_nextExpectedSeq);
                    _nextExpectedSeq++;
                    _totalProcessed++;
                }
                else
                {
                    break;
                }
            }
        }
        
        /// <summary>
        /// 检查缓冲区大小，如果超过阈值则强制输出
        /// </summary>
        private void CheckBufferSize()
        {
            if (_buffer.Count > _currentSize)
            {
                // 缓冲区过大，输出最旧的包
                var oldest = _buffer.First();
                _outputCallback(oldest.Value.Item);
                _buffer.Remove(oldest.Key);
                _totalProcessed++;
                
                // 跳过缺失的包
                if (oldest.Key > _nextExpectedSeq)
                {
                    _totalDropped += (ulong)(oldest.Key - _nextExpectedSeq);
                    _nextExpectedSeq = oldest.Key + 1;
                }
                
                _logger.LogDebug("Buffer overflow: forced output seq={Seq}, skipped={Skipped}",
                    oldest.Key, oldest.Key - _nextExpectedSeq + 1);
                
                // 动态调整队列大小（增大）
                if (_currentSize < _sizeMax)
                {
                    _currentSize = Math.Min(_currentSize + 1, _sizeMax);
                }
            }
        }
        
        /// <summary>
        /// 检查超时的包
        /// </summary>
        private void CheckTimeout()
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();
            
            foreach (var kvp in _buffer)
            {
                var elapsed = (now - kvp.Value.ArrivalTime).TotalMilliseconds;
                
                if (elapsed > _timeoutMs)
                {
                    // 超时，输出此包
                    _outputCallback(kvp.Value.Item);
                    toRemove.Add(kvp.Key);
                    _totalProcessed++;
                    
                    // 如果这个包的序列号大于期望值，说明中间有丢包
                    if (kvp.Key > _nextExpectedSeq)
                    {
                        _totalDropped += (ulong)(kvp.Key - _nextExpectedSeq);
                        _logger.LogDebug("Timeout: output seq={Seq}, skipped={Skipped}",
                            kvp.Key, kvp.Key - _nextExpectedSeq);
                    }
                    
                    _nextExpectedSeq = kvp.Key + 1;
                }
                else
                {
                    // 由于是排序字典，后面的包更新，不需要继续检查
                    break;
                }
            }
            
            // 移除已输出的包
            foreach (var seq in toRemove)
            {
                _buffer.Remove(seq);
            }
            
            // 尝试输出后续已就绪的包
            if (toRemove.Count > 0)
            {
                FlushReady();
            }
        }
        
        /// <summary>
        /// 判断序列号 a 是否在 b 之前（考虑循环）
        /// </summary>
        private bool IsSeqBefore(uint a, uint b)
        {
            // 处理 uint 序列号的循环
            // 假设序列号不会跳跃超过 2^31
            return (int)(a - b) < 0;
        }
        
        #endregion
        
        #region Inner Types
        
        /// <summary>
        /// 队列条目
        /// </summary>
        private class QueueEntry
        {
            public T Item { get; set; } = default!;
            public DateTime ArrivalTime { get; set; }
        }
        
        #endregion
    }
}

