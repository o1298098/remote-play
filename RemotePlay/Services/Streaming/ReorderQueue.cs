using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 丢弃策略（参考 chiaki-ng）
    /// </summary>
    public enum ReorderQueueDropStrategy
    {
        /// <summary>
        /// 丢弃最旧的包（队列开始处）
        /// </summary>
        Begin,
        /// <summary>
        /// 丢弃最新的包（队列结束处）
        /// </summary>
        End
    }

    /// <summary>
    /// 包重排序队列 - 处理乱序到达的 AV 包（参考 chiaki-ng 的 reorderqueue.c）
    /// 
    /// 工作原理：
    /// 1. 缓存乱序到达的包
    /// 2. 按序列号排序
    /// 3. 当缺失的包到达或超时后，按顺序输出包
    /// 4. 支持超时检查和丢弃策略，避免积压导致长卡顿
    /// </summary>
    public class ReorderQueue<T> where T : class
    {
        #region Constants
        
        // ✅ 配置常量（参考 chiaki-ng，视频流需要更大的缓冲区）
        private const int DEFAULT_SIZE_MIN = 8;        // 最小队列大小
        private const int DEFAULT_SIZE_MAX = 128;      // 最大队列大小（增大以应对网络抖动）
        private const int DEFAULT_SIZE_START = 32;     // 初始队列大小（增大以应对乱序）
        private const int DEFAULT_TIMEOUT_MS = 50;     // 超时时间（毫秒）
        
        #endregion
        
        #region Fields
        
        private const uint SEQ_MASK = 0xFFFF;
        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeqNum;     // 获取序列号的函数
        private readonly Action<T> _outputCallback;     // 输出回调
        private Action<T>? _dropCallback;    // 丢弃回调（参考 chiaki-ng 的 drop_cb，可动态设置）
        private Action? _timeoutCallback;    // ✅ 超时回调（用于检测持续超时）
        
        private readonly SortedDictionary<uint, QueueEntry> _buffer;  // 缓冲区（按序列号排序）
        private readonly object _lock = new object();
        
        private uint _nextExpectedSeq = 0;              // 期望的下一个序列号
        private int _currentSize;                       // 当前队列大小
        private readonly int _sizeMin;                  // 最小队列大小
        private readonly int _sizeMax;                  // 最大队列大小
        private readonly int _timeoutMs;                // 超时时间
        private ReorderQueueDropStrategy _dropStrategy; // 丢弃策略（参考 chiaki-ng）
        
        private bool _initialized = false;              // 是否已初始化
        
        // 统计信息
        private ulong _totalProcessed = 0;
        private ulong _totalDropped = 0;
        private ulong _totalReordered = 0;
        private ulong _totalTimeoutDropped = 0;        // 超时丢弃计数
        
        // ✅ 缓冲区满载检测（用于检测持续满载）
        private int _consecutiveFullDrops = 0;         // 连续满载丢弃计数
        private DateTime _lastFullDropTime = DateTime.MinValue; // 最后一次满载丢弃时间
        private const int MAX_CONSECUTIVE_FULL_DROPS = 20; // ✅ 最大连续满载丢弃次数（超过此次数触发恢复）
        private static readonly TimeSpan FULL_DROP_WINDOW = TimeSpan.FromSeconds(2); // ✅ 满载丢弃窗口（2秒内的丢弃才算连续）
        
        #endregion
        
        #region Constructor
        
        /// <summary>
        /// 创建重排序队列
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="getSeqNum">获取包序列号的函数</param>
        /// <param name="outputCallback">输出回调函数</param>
        /// <param name="dropCallback">丢弃回调函数（可选，参考 chiaki-ng 的 drop_cb）</param>
        /// <param name="sizeStart">初始队列大小</param>
        /// <param name="sizeMin">最小队列大小</param>
        /// <param name="sizeMax">最大队列大小</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <param name="dropStrategy">丢弃策略（参考 chiaki-ng）</param>
        /// <param name="timeoutCallback">超时回调函数（可选，用于检测持续超时）</param>
        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            Action<T>? dropCallback = null,
            int sizeStart = DEFAULT_SIZE_START,
            int sizeMin = DEFAULT_SIZE_MIN,
            int sizeMax = DEFAULT_SIZE_MAX,
            int timeoutMs = DEFAULT_TIMEOUT_MS,
            ReorderQueueDropStrategy dropStrategy = ReorderQueueDropStrategy.End,
            Action? timeoutCallback = null)
        {
            _logger = logger;
            _getSeqNum = getSeqNum;
            _outputCallback = outputCallback;
            _dropCallback = dropCallback;
            _timeoutCallback = timeoutCallback;
            
            _buffer = new SortedDictionary<uint, QueueEntry>();
            
            _currentSize = Math.Clamp(sizeStart, sizeMin, sizeMax);
            _sizeMin = sizeMin;
            _sizeMax = sizeMax;
            _timeoutMs = timeoutMs;
            _dropStrategy = dropStrategy;
            
            _logger.LogDebug("ReorderQueue created: size={Size} (min={Min}, max={Max}), timeout={Timeout}ms, dropStrategy={Strategy}",
                _currentSize, _sizeMin, _sizeMax, _timeoutMs, _dropStrategy);
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
                uint seqNum = _getSeqNum(item) & SEQ_MASK;
                
                // 首次初始化
                if (!_initialized)
                {
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    _logger.LogInformation("✅ ReorderQueue initialized with seq={Seq}", seqNum);
                    // ✅ 首次初始化时，直接输出第一个包（参考 chiaki-ng）
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    return;
                }
                
                // 计算当前队列的 end（参考 chiaki-ng: end = add(queue->begin, queue->count)）
                uint end = MaskSeq(_nextExpectedSeq + (uint)GetBufferCount());
                
                // 检查序列号是否在队列范围内（参考 chiaki-ng: ge(seq_num, queue->begin) && lt(seq_num, end)）
                // 注意：如果队列为空，end == _nextExpectedSeq，所以需要特殊处理
                bool inRange = GetBufferCount() == 0 
                    ? (seqNum == _nextExpectedSeq)  // 队列为空时，只有正好是期望的包才算在范围内
                    : (!IsSeqBefore(seqNum, _nextExpectedSeq) && IsSeqBefore(seqNum, end));
                
                // ✅ 添加诊断日志
                if (!inRange && GetBufferCount() == 0)
                {
                    uint gap = SequenceDistance(_nextExpectedSeq, seqNum);
                    _logger.LogDebug("🔍 Packet out of range (queue empty): seq={Seq}, expected={Expected}, gap={Gap}, end={End}",
                        seqNum, _nextExpectedSeq, gap, end);
                }
                
                if (inRange)
                {
                    // 包在队列范围内
                    if (_buffer.TryGetValue(seqNum, out var existingEntry))
                    {
                        if (existingEntry.IsSet)
                        {
                            // 重复包，丢弃（参考 chiaki-ng: entry->set == true）
                            _dropCallback?.Invoke(item);
                            _totalDropped++;
                            _logger.LogTrace("Dropped duplicate packet: seq={Seq}", seqNum);
                            return;
                        }
                        else
                        {
                            // 预留位置，现在包到达了（参考 chiaki-ng: entry->set = true）
                            existingEntry.Item = item;
                            existingEntry.ArrivalTime = DateTime.UtcNow;
                            _logger.LogTrace("Packet arrived at reserved slot: seq={Seq}", seqNum);
                            
                            // 如果正好是期望的包，直接输出
                            if (seqNum == _nextExpectedSeq)
                            {
                                _outputCallback(item);
                                AdvanceExpected();
                                _totalProcessed++;
                                FlushReady();
                            }
                            return;
                        }
                    }
                    else
                    {
                        // ✅ 包在范围内但缓冲区中没有对应条目（队列为空的情况）
                        // 直接添加并处理这个包
                        _buffer[seqNum] = new QueueEntry
                        {
                            Item = item,
                            ArrivalTime = DateTime.UtcNow
                        };
                        
                        // 如果正好是期望的包，直接输出
                        if (seqNum == _nextExpectedSeq)
                        {
                            _outputCallback(item);
                            _buffer.Remove(seqNum);
                            AdvanceExpected();
                            _totalProcessed++;
                            FlushReady();
                        }
                        return;
                    }
                }
                
                // 检查序列号是否过期（参考 chiaki-ng: lt(seq_num, queue->begin)）
                // 注意：如果队列为空且包序列号与期望值差距很大，可能是重置后的序列号跳跃，应该扩展队列而不是丢弃
                if (IsSeqBefore(seqNum, _nextExpectedSeq))
                {
                    // ✅ 计算序列号差距
                    uint gap = SequenceDistance(seqNum, _nextExpectedSeq);
                    
                // ✅ 如果队列为空且未初始化，允许任何序列号作为起始点（重置后的情况）
                if (GetBufferCount() == 0 && !_initialized)
                {
                    // 队列为空且未初始化，允许任何序列号重新初始化（重置后的情况）
                    _logger.LogInformation("✅ Queue empty and uninitialized, accepting seq={Seq} as starting point (expected was {Expected}, gap={Gap})",
                        seqNum, _nextExpectedSeq, gap);
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    // 直接输出这个包
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    return;
                }
                
                // ✅ 如果队列为空但已初始化，允许序列号跳跃（可能是重置后的关键帧）
                if (GetBufferCount() == 0)
                {
                    // 队列为空，允许重新初始化（重置后的关键帧可能序列号很大或很小）
                    // 如果差距不大（< 100），可能是正常的序列号回绕
                    if (gap < 100)
                    {
                        _logger.LogDebug("Queue empty but seq={Seq} < expected={Expected}, small gap={Gap}, accepting packet (likely after reset)",
                            seqNum, _nextExpectedSeq, gap);
                        _nextExpectedSeq = seqNum;
                        // 直接输出这个包
                        _outputCallback(item);
                        AdvanceExpected();
                        _totalProcessed++;
                        return;
                    }
                    else
                    {
                        // 差距较大，可能是序列号回绕或错误，重新初始化
                        _logger.LogWarning("⚠️ Queue empty but seq={Seq} < expected={Expected}, large gap={Gap}, reinitializing queue (likely after reset)",
                            seqNum, _nextExpectedSeq, gap);
                        _nextExpectedSeq = seqNum;
                        _initialized = true;
                        // 直接输出这个包
                        _outputCallback(item);
                        AdvanceExpected();
                        _totalProcessed++;
                        return;
                    }
                }
                
                // ✅ 如果序列号差距过大（> 1000），可能是序列号回绕或队列状态错误，重置队列
                // 这通常发生在网络延迟或序列号回绕时
                if (gap > 1000)
                {
                    _logger.LogWarning("⚠️ Large sequence gap detected (gap={Gap}), resetting queue: seq={Seq}, expected={Expected}, buffer_count={Count}",
                        gap, seqNum, _nextExpectedSeq, GetBufferCount());
                    
                    // 清空缓冲区并重新初始化
                    _buffer.Clear();
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    
                    // 直接输出这个包
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    return;
                }
                
                // 包已过期，丢弃
                _dropCallback?.Invoke(item);
                _totalDropped++;
                _logger.LogWarning("⚠️ Dropped late packet: seq={Seq}, expected={Expected}, gap={Gap}, buffer_count={Count}", 
                    seqNum, _nextExpectedSeq, gap, GetBufferCount());
                return;
            }
            
            // => ge(seq_num, end) == true（参考 chiaki-ng）
            // 包在队列范围外，需要扩展队列
            
            // ✅ 关键修复：如果队列为空，根据序列号差距决定处理方式
            // 这通常发生在重置后，第一个到达的包序列号很大（如关键帧）
            if (GetBufferCount() == 0)
            {
                uint gap = SequenceDistance(_nextExpectedSeq, seqNum);
                
                // ✅ 设置合理的阈值：
                // - gap <= 5：直接接受（正常顺序超前）
                // - gap 6-100：扩展缓冲区等待（包可能还在传输中）
                // - gap 101-500：记录警告但接受（中间包可能已丢失）
                // - gap > 500：直接重新初始化（包确实已丢失）
                const uint SMALL_GAP_THRESHOLD = 5;    // 小差距：直接接受
                const uint MEDIUM_GAP_THRESHOLD = 100; // 中等差距：扩展缓冲区等待中间包
                const uint LARGE_GAP_THRESHOLD = 500;  // 大差距：直接重新初始化（包已丢失）
                
                if (gap <= SMALL_GAP_THRESHOLD)
                {
                    // ✅ 序列号差距很小（<= 5），直接接受并输出这个包（连续包）
                    _logger.LogDebug("🔍 Queue empty, small gap (gap={Gap}), accepting and outputting: seq={Seq}, expected={Expected}",
                        gap, seqNum, _nextExpectedSeq);
                    
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    FlushReady();
                    return;
                }
                else if (gap > LARGE_GAP_THRESHOLD)
                {
                    // ✅ 序列号差距太大（> 200），说明中间包已丢失，直接重新初始化
                    // 这种情况下，继续等待中间包没有意义，直接接受新包
                    _logger.LogWarning("⚠️ Queue empty but seq gap very large (gap={Gap} > {Threshold}), reinitializing (packets likely lost): seq={Seq}, expected={Expected}",
                        gap, LARGE_GAP_THRESHOLD, seqNum, _nextExpectedSeq);
                    
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    FlushReady();
                    return;
                }
                else if (gap <= MEDIUM_GAP_THRESHOLD)
                {
                    // ✅ gap 在 6-100 之间，扩展缓冲区等待中间包
                    // 这样可以等待中间包到达，避免直接跳过导致帧不完整
                    if (gap <= (uint)_sizeMax)
                    {
                        // 中等差距（6-100），且不超过最大大小，扩展缓冲区
                        int oldSize = _currentSize;
                        int newSize = Math.Min((int)gap + 20, _sizeMax); // 扩展以容纳 gap + 额外缓冲
                        if (newSize > _currentSize)
                        {
                            _currentSize = newSize;
                            _logger.LogInformation("📈 Queue empty, medium gap (gap={Gap}), expanding buffer: {Old} -> {New} to wait for missing packets",
                                gap, oldSize, _currentSize);
                        }
                        // ✅ 重新计算 end，确保扩展缓冲区后正确计算范围
                        end = MaskSeq(_nextExpectedSeq + (uint)GetBufferCount());
                        // 继续执行扩展队列逻辑，将包放入缓冲区等待
                    }
                    else
                    {
                        // gap 超过最大大小，但仍然尝试扩展（但不能超过限制）
                        _logger.LogWarning("⚠️ Queue empty, medium gap (gap={Gap}) exceeds max size ({MaxSize}), accepting packet: seq={Seq}, expected={Expected}",
                            gap, _sizeMax, seqNum, _nextExpectedSeq);
                        
                        _nextExpectedSeq = seqNum;
                        _initialized = true;
                        _outputCallback(item);
                        AdvanceExpected();
                        _totalProcessed++;
                        FlushReady();
                        return;
                    }
                    // gap <= MEDIUM_GAP_THRESHOLD 且 > SMALL_GAP_THRESHOLD，继续扩展队列逻辑
                }
                else
                {
                    // gap 在 101-500 之间，记录警告但接受（中间包可能已丢失，但可能还在传输中）
                    // 对于这个范围的差距，我们仍然尝试接受，因为等待可能没有意义
                    // ✅ 但是如果 gap 超过当前缓冲区大小，直接重新初始化（重置后的情况）
                    if (gap > (uint)_currentSize)
                    {
                        _logger.LogWarning("⚠️ Queue empty, gap ({Gap}) exceeds buffer size ({Size}), reinitializing queue: seq={Seq}, expected={Expected}",
                            gap, _currentSize, seqNum, _nextExpectedSeq);
                        
                        _buffer.Clear();
                        _nextExpectedSeq = seqNum;
                        _initialized = true;
                        _outputCallback(item);
                        AdvanceExpected();
                        _totalProcessed++;
                        FlushReady();
                        return;
                    }
                    
                    _logger.LogWarning("⚠️ Queue empty, large gap (gap={Gap}), accepting packet directly (missing packets may be lost): seq={Seq}, expected={Expected}",
                        gap, seqNum, _nextExpectedSeq);
                    
                    _nextExpectedSeq = seqNum;
                    _initialized = true;
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    FlushReady();
                    return;
                }
            }
            
            // 计算需要的空间（参考 chiaki-ng: lt(total_end, new_end)）
            uint newEnd = MaskSeq(seqNum + 1);
            uint freeElems = (uint)(_currentSize - GetBufferCount());
            uint totalEnd = MaskSeq(end + freeElems);
            
            // ✅ 检查缓冲区是否已满（参考 chiaki-ng: lt(total_end, new_end)）
            if (IsSeqBefore(totalEnd, newEnd))
                {
                    if (_dropStrategy == ReorderQueueDropStrategy.End)
                    {
                        // 丢弃新包（参考 chiaki-ng: DROP_STRATEGY_END）
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        _logger.LogWarning("⚠️ Dropped new packet (END strategy): seq={Seq}, buffer_count={Count}, expected={Expected}", 
                            seqNum, GetBufferCount(), _nextExpectedSeq);
                        return;
                    }
                    
                    // ✅ 优先尝试增大缓冲区，而不是立即丢弃（参考 chiaki-ng）
                    if (_currentSize < _sizeMax)
                    {
                        int oldSize = _currentSize;
                        int newSize = Math.Min(_currentSize + 16, _sizeMax);
                        if (newSize > _currentSize)
                        {
                            _currentSize = newSize;
                            freeElems = (uint)(_currentSize - GetBufferCount());
                            totalEnd = MaskSeq(end + freeElems);
                            _logger.LogDebug("Buffer size increased dynamically: {Old} -> {New} (count={Count})", 
                                oldSize, _currentSize, GetBufferCount());
                        }
                    }
                    
                    // ✅ 如果缓冲区仍满，丢弃最旧的包（参考 chiaki-ng: drop first until empty or enough space）
                    while (GetBufferCount() > 0 && IsSeqBefore(totalEnd, newEnd))
                    {
                        var oldestSeq = GetOldestSequence();
                        if (oldestSeq == null)
                            break;
                            
                        uint oldest = oldestSeq.Value;
                        if (_buffer.TryGetValue(oldest, out var oldestEntry) && oldestEntry.IsSet)
                        {
                            // 只丢弃已到达的包（参考 chiaki-ng: if(entry->set && queue->drop_cb)）
                            _dropCallback?.Invoke(oldestEntry.Item!);
                        }
                        
                        _buffer.Remove(oldest);
                        _nextExpectedSeq = MaskSeq(oldest + 1);
                        _totalDropped++;
                        
                        // 重新计算
                        end = MaskSeq(_nextExpectedSeq + (uint)GetBufferCount());
                        freeElems = (uint)(_currentSize - GetBufferCount());
                        totalEnd = MaskSeq(end + freeElems);
                        
                        // ✅ 检测连续满载丢弃
                        var now = DateTime.UtcNow;
                        if (_lastFullDropTime != DateTime.MinValue && (now - _lastFullDropTime) < FULL_DROP_WINDOW)
                        {
                            _consecutiveFullDrops++;
                        }
                        else
                        {
                            _consecutiveFullDrops = 1;
                        }
                        _lastFullDropTime = now;
                    }
                    
                    // ✅ 如果队列为空，直接跳转到新序列号（参考 chiaki-ng: if(queue->count == 0) queue->begin = seq_num）
                    if (GetBufferCount() == 0)
                    {
                        _nextExpectedSeq = seqNum;
                        end = seqNum;
                    }
                    
                    // ✅ 连续满载丢弃次数过多，触发恢复回调
                    if (_consecutiveFullDrops >= MAX_CONSECUTIVE_FULL_DROPS)
                    {
                        _logger.LogWarning("⚠️ 检测到持续缓冲区满载（连续 {Count} 次，窗口 {Window}s），触发恢复策略",
                            _consecutiveFullDrops, FULL_DROP_WINDOW.TotalSeconds);
                        _timeoutCallback?.Invoke();
                        _consecutiveFullDrops = 0;
                        _lastFullDropTime = DateTime.MinValue;
                    }
                }
                
                // ✅ 扩展队列到 newEnd，预留中间的位置（参考 chiaki-ng: move end until new_end）
                // 注意：end 应该从当前队列的结束位置开始扩展，而不是每次都重新计算
                // ✅ 关键修复：在扩展之前，检查是否需要添加的预留位置数量是否超过限制
                end = MaskSeq(_nextExpectedSeq + (uint)GetBufferCount());
                
                // 计算需要添加的预留位置数量
                uint slotsToAdd = SequenceDistance(end, newEnd);
                
                // ✅ 如果添加预留位置后，缓冲区计数会超过限制，应该触发丢弃策略
                if (GetBufferCount() + slotsToAdd > _currentSize)
                {
                    // 如果使用 END 策略，直接丢弃新包
                    if (_dropStrategy == ReorderQueueDropStrategy.End)
                    {
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        _logger.LogWarning("⚠️ Dropped new packet (END strategy): seq={Seq}, buffer_count={Count}, slots_to_add={Slots}, current_size={Size}", 
                            seqNum, GetBufferCount(), slotsToAdd, _currentSize);
                        return;
                    }
                    
                    // ✅ 使用 BEGIN 策略：丢弃最旧的包，直到有足够空间
                    while (GetBufferCount() + slotsToAdd > _currentSize && GetBufferCount() > 0)
                    {
                        var oldestSeq = GetOldestSequence();
                        if (oldestSeq == null)
                            break;
                            
                        uint oldest = oldestSeq.Value;
                        if (_buffer.TryGetValue(oldest, out var oldestEntry) && oldestEntry.IsSet)
                        {
                            _dropCallback?.Invoke(oldestEntry.Item!);
                        }
                        
                        _buffer.Remove(oldest);
                        _nextExpectedSeq = MaskSeq(oldest + 1);
                        _totalDropped++;
                        
                        // 重新计算 end 和 slotsToAdd
                        end = MaskSeq(_nextExpectedSeq + (uint)GetBufferCount());
                        slotsToAdd = SequenceDistance(end, newEnd);
                    }
                    
                    // ✅ 如果队列为空，直接跳转到新序列号
                    if (GetBufferCount() == 0)
                    {
                        _nextExpectedSeq = seqNum;
                        end = seqNum;
                        slotsToAdd = 0;
                    }
                }
                
                // ✅ 现在安全地扩展队列，添加预留位置
                while (IsSeqBefore(end, newEnd) && GetBufferCount() < _currentSize)
                {
                    // 预留位置（参考 chiaki-ng: queue->queue[idx(end)].set = false）
                    if (!_buffer.ContainsKey(end))
                    {
                        _buffer[end] = new QueueEntry
                        {
                            Item = null, // 预留位置，包还未到达
                            ArrivalTime = DateTime.MinValue
                        };
                    }
                    // ✅ 递增 end（参考 chiaki-ng: end = add(end, 1)）
                    end = MaskSeq(end + 1);
                }
                
                // ✅ 如果仍然无法扩展（序列号差距太大），丢弃新包
                if (IsSeqBefore(end, newEnd))
                {
                    _dropCallback?.Invoke(item);
                    _totalDropped++;
                    _logger.LogWarning("⚠️ Dropped new packet (sequence gap too large): seq={Seq}, expected={Expected}, buffer_count={Count}, max_size={Max}", 
                        seqNum, _nextExpectedSeq, GetBufferCount(), _currentSize);
                    return;
                }
                
                // ✅ 设置包（参考 chiaki-ng: entry->set = true, entry->user = user）
                if (_buffer.TryGetValue(seqNum, out var entry))
                {
                    entry.Item = item;
                    entry.ArrivalTime = DateTime.UtcNow;
                }
                else
                {
                    // 不应该发生，但为了安全还是处理
                    _buffer[seqNum] = new QueueEntry
                    {
                        Item = item,
                        ArrivalTime = DateTime.UtcNow
                    };
                }
                
                // ✅ 如果正好是期望的包，直接输出（参考 chiaki-ng: pull）
                if (seqNum == _nextExpectedSeq)
                {
                    _outputCallback(item);
                    AdvanceExpected();
                    _totalProcessed++;
                    FlushReady();
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
        public (ulong processed, ulong dropped, ulong reordered, ulong timeoutDropped, int bufferSize) GetStats()
        {
            lock (_lock)
            {
                // 统计已到达的包数量（不包括预留位置）
                int arrivedCount = _buffer.Values.Count(e => e.IsSet);
                return (_totalProcessed, _totalDropped, _totalReordered, _totalTimeoutDropped, arrivedCount);
            }
        }
        
        /// <summary>
        /// 设置丢弃策略（参考 chiaki-ng）
        /// </summary>
        public void SetDropStrategy(ReorderQueueDropStrategy strategy)
        {
            lock (_lock)
            {
                _dropStrategy = strategy;
                _logger.LogDebug("ReorderQueue drop strategy changed to {Strategy}", strategy);
            }
        }
        
        /// <summary>
        /// 设置丢弃回调（参考 chiaki-ng 的 drop_cb）
        /// </summary>
        public void SetDropCallback(Action<T>? callback)
        {
            lock (_lock)
            {
                _dropCallback = callback;
            }
        }

        /// <summary>
        /// 设置超时回调（用于检测持续超时和持续满载）
        /// 注意：这个方法可以被多次调用以添加多个回调（链式调用）
        /// </summary>
        public void SetTimeoutCallback(Action? callback)
        {
            if (callback == null)
                return;
                
            // ✅ 支持多次调用，合并回调（先调用原有回调，再调用新回调）
            var oldCallback = _timeoutCallback;
            if (oldCallback != null)
            {
                _timeoutCallback = () =>
                {
                    oldCallback();
                    callback();
                };
            }
            else
            {
                _timeoutCallback = callback;
            }
        }

        /// <summary>
        /// 重置统计信息（用于恢复后重置状态）
        /// </summary>
        public void ResetStats()
        {
            lock (_lock)
            {
                _consecutiveFullDrops = 0;
                _lastFullDropTime = DateTime.MinValue;
            }
        }
        
        /// <summary>
        /// 重置队列状态，允许重新初始化（用于队列重置后）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                uint oldExpected = _nextExpectedSeq;
                _buffer.Clear();
                _initialized = false;
                _nextExpectedSeq = 0;
                _consecutiveFullDrops = 0;
                _lastFullDropTime = DateTime.MinValue;
                _logger.LogInformation("🔄 ReorderQueue reset: cleared buffer, old_expected={OldExpected}, reinitialization flag cleared", oldExpected);
            }
        }
        
        #endregion
        
        #region Private Methods
        
        /// <summary>
        /// 输出缓冲区中已就绪的包（参考 chiaki-ng: chiaki_reorder_queue_pull）
        /// </summary>
        private void FlushReady()
        {
            while (GetBufferCount() > 0)
            {
                // 检查下一个期望的包是否在缓冲区中且已到达（参考 chiaki-ng: if(!entry->set) return false）
                if (_buffer.TryGetValue(_nextExpectedSeq, out var entry))
                {
                    if (!entry.IsSet)
                    {
                        // 包还未到达，停止输出（参考 chiaki-ng）
                        break;
                    }
                    
                    // 包已到达，输出（参考 chiaki-ng: entry->set == true）
                    _outputCallback(entry.Item!);
                    _buffer.Remove(_nextExpectedSeq);
                    AdvanceExpected();
                    _totalProcessed++;
                }
                else
                {
                    break;
                }
            }
        }
        
        /// <summary>
        /// 获取缓冲区中包的数量（包括预留位置，参考 chiaki-ng: queue->count）
        /// </summary>
        private int GetBufferCount()
        {
            return _buffer.Count;
        }
        
        /// <summary>
        /// 获取最旧的序列号（用于丢弃策略）
        /// </summary>
        private uint? GetOldestSequence()
        {
            if (_buffer.Count == 0)
                return null;
                
            // 找到最旧的已到达的包
            foreach (var kvp in _buffer)
            {
                if (kvp.Value.IsSet)
                    return kvp.Key;
            }
            
            // 如果没有已到达的包，返回第一个预留位置
            return _buffer.Keys.FirstOrDefault();
        }
        
        /// <summary>
        /// 检查缓冲区大小，如果超过阈值则根据丢弃策略处理（参考 chiaki-ng）
        /// 注意：此方法现在主要用于动态调整缓冲区大小，实际丢弃逻辑在 Push 中处理
        /// </summary>
        private void CheckBufferSize()
        {
            int bufferCount = GetBufferCount();
            if (bufferCount <= _currentSize)
                return;

            // ✅ 优先尝试增大缓冲区，而不是立即丢弃（参考 chiaki-ng）
            if (_currentSize < _sizeMax)
            {
                // 快速增大缓冲区（每次增加 8）
                int newSize = Math.Min(_currentSize + 8, _sizeMax);
                if (newSize > _currentSize)
                {
                    _currentSize = newSize;
                    _logger.LogDebug("Buffer size increased: {Old} -> {New} (count={Count})", 
                        _currentSize - 8, _currentSize, bufferCount);
                    return; // 增大后可能不需要丢弃
                }
            }

            // 缓冲区已到最大，记录警告（实际丢弃在 Push 中处理）
            _logger.LogWarning("⚠️ Buffer size at maximum: count={Count}, size={Size}/{Max}",
                bufferCount, _currentSize, _sizeMax);
        }
        
        /// <summary>
        /// 检查超时的包（参考 chiaki-ng，但使用时间戳而非轮询）
        /// 注意：只检查已到达的包，预留位置不参与超时检查
        /// </summary>
        private void CheckTimeout()
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();
            
            // ✅ 只检查已到达的包（参考 chiaki-ng: if(!entry->set) return false）
            foreach (var kvp in _buffer)
            {
                // 跳过预留位置（包还未到达）
                if (!kvp.Value.IsSet)
                {
                    // 如果是期望的包但还未到达，检查是否超时（从期望时间开始计算）
                    if (kvp.Key == _nextExpectedSeq)
                    {
                        // 预留位置超时：跳过这个位置，继续下一个
                        _logger.LogWarning("Timeout: reserved slot seq={Seq} never received, skipping", kvp.Key);
                        toRemove.Add(kvp.Key);
                        AdvanceExpected();
                        _totalDropped++;
                        _totalTimeoutDropped++;
                        _timeoutCallback?.Invoke();
                        break; // 只处理第一个期望的包
                    }
                    continue;
                }
                
                var elapsed = (now - kvp.Value.ArrivalTime).TotalMilliseconds;
                
                if (elapsed > _timeoutMs)
                {
                    // 超时，输出此包（参考 chiaki-ng: pull）
                    _outputCallback(kvp.Value.Item!);
                    toRemove.Add(kvp.Key);
                    _totalProcessed++;
                    
                    // 如果这个包的序列号大于期望值，说明中间有丢包
                    uint skipped = SequenceDistance(_nextExpectedSeq, kvp.Key);
                    if (skipped != 0)
                    {
                        _totalDropped += skipped;
                        _totalTimeoutDropped += skipped;
                        _logger.LogWarning("Timeout: output seq={Seq}, skipped={Skipped}, elapsed={Elapsed}ms",
                            kvp.Key, skipped, elapsed);
                    }
                    else
                    {
                        _logger.LogDebug("Timeout: output seq={Seq}, elapsed={Elapsed}ms",
                            kvp.Key, elapsed);
                    }
                    
                    _nextExpectedSeq = MaskSeq(kvp.Key + 1);
                    
                    // ✅ 触发超时回调（用于检测持续超时）
                    _timeoutCallback?.Invoke();
                    
                    // 由于是排序字典，只处理第一个超时的包
                    break;
                }
                else
                {
                    // 由于是排序字典，后面的包更新，不需要继续检查
                    break;
                }
            }
            
            // 移除已输出的包和超时的预留位置
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
        /// 判断序列号 a 是否在 b 之前（考虑循环，参考 chiaki-ng: chiaki_seq_num_16_lt）
        /// </summary>
        private bool IsSeqBefore(uint a, uint b)
        {
            if (a == b)
                return false;
            
            // 参考 chiaki-ng: chiaki_seq_num_16_lt
            // 使用有符号整数差值来判断，正确处理循环
            int diff = (int)(b & SEQ_MASK) - (int)(a & SEQ_MASK);
            
            // 如果 a < b 且差值小于 0x8000，则 a 在 b 之前
            // 如果 a > b 且差值的绝对值大于 0x8000，则 a 在 b 之前（循环）
            if (a < b)
            {
                return diff < 0x8000;
            }
            else
            {
                return -diff > 0x8000;
            }
        }

        private uint SequenceDistance(uint from, uint to)
        {
            return (to - from) & SEQ_MASK;
        }

        private static uint MaskSeq(uint value) => value & SEQ_MASK;

        private void AdvanceExpected()
        {
            _nextExpectedSeq = MaskSeq(_nextExpectedSeq + 1);
        }
        
        #endregion
        
        #region Inner Types
        
        /// <summary>
        /// 队列条目（参考 chiaki-ng: ChiakiReorderQueueEntry）
        /// </summary>
        private class QueueEntry
        {
            public T? Item { get; set; } // null 表示预留位置（包还未到达），参考 chiaki-ng: entry->set = false
            public DateTime ArrivalTime { get; set; } // 包到达时间（仅当 Item != null 时有效）
            
            /// <summary>
            /// 检查包是否已到达（参考 chiaki-ng: entry->set）
            /// </summary>
            public bool IsSet => Item != null;
        }
        
        #endregion
    }
}

