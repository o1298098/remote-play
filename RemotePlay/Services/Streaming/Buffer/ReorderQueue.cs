using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// Production-ready reorder queue for 16-bit cyclic sequence numbers (0..65535).
    /// Key design choices made to avoid frame-jitter / accidental window resets:
    /// - No aggressive resets. Only reset on strong wrap-around evidence.
    /// - Conservative recovery distance (small, e.g. 200 seqs) to avoid accepting very old packets.
    /// - Limit outputs per Pull to avoid "bursting" dozens of frames at once.
    /// - Optional predicate to mark keyframes (to preserve them where possible).
    /// - Thread-safe via a single lock; lock hold-time kept small.
    /// - Exposes hooks for drop/timeout/output for integration.
    ///
    /// Generic T must be a reference type.
    /// </summary>
    public enum ReorderQueueDropStrategy
    {
        End,
        Begin
    }

    public sealed class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFFu;
        private const uint HALF_SPACE = 0x8000u; // 32768

        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeqNum; // returns raw seq (assumed 0..65535)
        private readonly Action<T> _outputCallback;
        private Action<T>? _dropCallback;
        private Action? _timeoutCallback;
        private readonly Func<T, bool>? _isKeyFrame; // optional, true if item is a keyframe

        private readonly Dictionary<uint, QueueEntry> _buffer = new Dictionary<uint, QueueEntry>();
        private readonly object _lock = new object();

        private uint _begin = 0; // next expected seq
        private int _count = 0;  // number of logical slots in window (reserved + arrived)
        private int _windowSize;
        private readonly int _sizeMin;
        private readonly int _sizeMax;
        private readonly int _timeoutMs;
        private ReorderQueueDropStrategy _dropStrategy;

        private bool _initialized = false;

        private ulong _totalProcessed = 0;
        private ulong _totalDropped = 0;
        private ulong _totalTimeoutDropped = 0;

        // conservative constants
        private const uint MAX_RECOVERY_DISTANCE = 200; // allow limited recovery when seq < begin
        private const int DEFAULT_MAX_OUTPUT_PER_PULL = 3; // avoid burst output
        private const int MAX_OUTPUT_PER_PULL = 20; // maximum dynamic output per pull

        private readonly int _maxOutputPerPull;
        
        // Log throttling
        private DateTime _lastDropLogTime = DateTime.MinValue;
        private int _dropLogCount = 0;
        private const int DROP_LOG_THROTTLE_MS = 1000; // log at most once per second
        private const int DROP_LOG_THROTTLE_COUNT = 50; // or when count exceeds this

        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            Action<T>? dropCallback = null,
            Func<T, bool>? isKeyFrame = null,
            int sizeStart = 32,
            int sizeMin = 8,
            int sizeMax = 128,
            int timeoutMs = 80,
            ReorderQueueDropStrategy dropStrategy = ReorderQueueDropStrategy.End,
            int maxOutputPerPull = DEFAULT_MAX_OUTPUT_PER_PULL,
            Action? timeoutCallback = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _getSeqNum = getSeqNum ?? throw new ArgumentNullException(nameof(getSeqNum));
            _outputCallback = outputCallback ?? throw new ArgumentNullException(nameof(outputCallback));
            _dropCallback = dropCallback;
            _isKeyFrame = isKeyFrame;
            _timeoutCallback = timeoutCallback;

            _sizeMin = Math.Max(4, sizeMin);
            _sizeMax = Math.Max(_sizeMin, sizeMax);
            _windowSize = Math.Clamp(sizeStart, _sizeMin, _sizeMax);
            _timeoutMs = Math.Max(10, timeoutMs);
            _dropStrategy = dropStrategy;
            _maxOutputPerPull = Math.Max(1, maxOutputPerPull);
        }

        #region Public API

        public void Push(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            lock (_lock)
            {
                uint seq = MaskSeq(_getSeqNum(item));

                if (!_initialized)
                {
                    _initialized = true;
                    _begin = seq;
                    _buffer[seq] = new QueueEntry { Item = item, ArrivalTime = DateTime.UtcNow };
                    _count = 1;
                    PullLockedLimited();
                    return;
                }

                // If seq is inside current logical window -> place or replace
                if (SeqInWindow(seq, _begin, (uint)_count))
                {
                    PlaceIntoWindow(seq, item);
                    if (seq == _begin)
                        PullLockedLimited();
                    return;
                }

                // ✅ 原则1：使用严格的数学定义判断是否是旧包
                // seq 比 begin "旧" <==> (seq - begin) mod 65536 >= 32768
                // 即：!IsNewer(seq, begin)
                if (!IsNewer(seq, _begin))
                {
                    // ✅ 原则2：旧包只丢弃，不尝试恢复，不检测回绕
                    // 回绕是自然发生的，当seq从65535增长到0时，IsNewer会自动处理
                    uint gap = SequenceDistance(_begin, seq);
                    
                    // Throttled logging
                    _dropLogCount++;
                    var now = DateTime.UtcNow;
                    bool shouldLog = (now - _lastDropLogTime).TotalMilliseconds > DROP_LOG_THROTTLE_MS || _dropLogCount >= DROP_LOG_THROTTLE_COUNT;
                    if (shouldLog)
                    {
                        _logger.LogWarning("ReorderQueue: dropping old packet (seq={Seq}, begin={Begin}, gap={Gap}, count={Count})", 
                            seq, _begin, gap, _dropLogCount);
                        _lastDropLogTime = now;
                        _dropLogCount = 0;
                    }

                    _dropCallback?.Invoke(item);
                    _totalDropped++;
                    return;
                }

                uint end = MaskSeq(_begin + (uint)_count);
                uint newEnd = MaskSeq(seq + 1u);
                uint needed = SequenceDistance(end, newEnd);

                // ✅ 原则3：不再检测回绕，回绕是自然发生的
                // IsNewer已经正确处理了回绕情况，无需特殊处理

                // ⚠️ 优化：尝试动态扩展窗口以适应更大的序列号跳跃
                if (needed > (uint)(_windowSize - _count) && needed <= (uint)_sizeMax)
                {
                    // 尝试扩展窗口以容纳新包
                    int newWindowSize = Math.Min(_sizeMax, (int)needed + _count + 50); // 添加一些缓冲
                    if (newWindowSize > _windowSize)
                    {
                        _logger.LogDebug("ReorderQueue: expanding window from {Old} to {New} to accommodate needed={Needed}",
                            _windowSize, newWindowSize, needed);
                        _windowSize = newWindowSize;
                    }
                }

                // ⚠️ 关键优化：如果窗口使用率超过80%，先尝试清理超时的包（避免死锁，直接内联）
                double windowUsageRate = _windowSize > 0 ? (double)_count / _windowSize : 0;
                if (needed > (uint)(_windowSize - _count) && windowUsageRate > 0.8)
                {
                    // 窗口使用率过高，先清理超时包（简化版本，只清理头部几个）
                    int cleaned = 0;
                    uint checkSeq = _begin;
                    var now = DateTime.UtcNow;
                    int maxCleanCheck = Math.Min(20, _count); // 最多检查20个
                    
                    for (int i = 0; i < maxCleanCheck && cleaned < 10; i++)
                    {
                        if (_buffer.TryGetValue(checkSeq, out var checkEntry) && checkEntry.IsSet)
                        {
                            var elapsed = (now - checkEntry.ArrivalTime).TotalMilliseconds;
                            // 严重积压时，缩短超时时间
                            double aggressiveTimeout = windowUsageRate > 0.9 ? _timeoutMs * 0.5 : _timeoutMs * 0.7;
                            if (elapsed > aggressiveTimeout)
                            {
                                // 输出超时的包
                                _outputCallback(checkEntry.Item!);
                                _buffer.Remove(checkSeq);
                                _totalProcessed++;
                                cleaned++;
                            }
                            else
                            {
                                break; // 遇到未超时的包，停止清理
                            }
                        }
                        else if (!_buffer.TryGetValue(checkSeq, out _))
                        {
                            // 遇到空洞，跳过
                            cleaned++;
                        }
                        else
                        {
                            break; // 遇到未设置的保留槽，停止清理
                        }
                        
                        checkSeq = MaskSeq(checkSeq + 1u);
                    }
                    
                    if (cleaned > 0)
                    {
                        _begin = checkSeq;
                        _count -= cleaned;
                        // 清理后重新计算可用空间
                        end = MaskSeq(_begin + (uint)_count);
                        needed = SequenceDistance(end, MaskSeq(seq + 1u));
                    }
                }

                // ✅ 原则4：Reset只在队列满到极限且begin无法推进时进行
                // 检查是否需要重置：窗口已扩展到最大，且begin处没有包，无法推进
                if (needed > (uint)(_windowSize - _count) && _windowSize >= _sizeMax)
                {
                    // 检查begin是否可以推进
                    bool canAdvanceBegin = false;
                    if (_count > 0)
                    {
                        // 检查begin处是否有包，或者可以超时清理
                        if (_buffer.TryGetValue(_begin, out var headEntry))
                        {
                            if (headEntry.IsSet)
                            {
                                // begin处有包，可以输出后推进
                                canAdvanceBegin = true;
                            }
                            else if (headEntry.ReservedTime != DateTime.MinValue)
                            {
                                // 检查是否超时
                                var elapsed = (DateTime.UtcNow - headEntry.ReservedTime).TotalMilliseconds;
                                if (elapsed > _timeoutMs * 2.0)
                                {
                                    canAdvanceBegin = true; // 可以超时清理
                                }
                            }
                        }
                    }

                    // 只有在无法推进时才重置
                    if (!canAdvanceBegin)
                    {
                        _logger.LogWarning("ReorderQueue: resetting window (queue full, cannot advance begin) (seq={Seq}, begin={Begin}, window={Window}, needed={Needed})",
                            seq, _begin, _windowSize, needed);
                        _buffer.Clear();
                        _begin = seq;
                        _count = 0;
                        _initialized = true;
                        _outputCallback(item);
                        _totalProcessed++;
                        return;
                    }
                }

                // Log when dropping packets due to insufficient space
                if (needed > (uint)(_windowSize - _count))
                {
                    // Not enough free space
                    if (_dropStrategy == ReorderQueueDropStrategy.End)
                    {
                        // Throttled logging
                        _dropLogCount++;
                        var now = DateTime.UtcNow;
                        bool shouldLog = (now - _lastDropLogTime).TotalMilliseconds > DROP_LOG_THROTTLE_MS || _dropLogCount >= DROP_LOG_THROTTLE_COUNT;
                        if (shouldLog)
                        {
                            _logger.LogWarning("ReorderQueue: dropping packet (insufficient space) (seq={Seq}, begin={Begin}, end={End}, needed={Needed}, window={Window}, count={Count}, free={Free}, dropCount={DropCount})",
                                seq, _begin, end, needed, _windowSize, _count, _windowSize - _count, _dropLogCount);
                            _lastDropLogTime = now;
                            _dropLogCount = 0;
                        }
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        return;
                    }

                    // Try to free space from the beginning but do so conservatively: we prefer dropping non-key slots
                    uint freeNeeded = needed - (uint)(_windowSize - _count);
                    FreeSpaceFromBeginConservatively(freeNeeded);

                    // Recompute after freeing
                    if (needed > (uint)(_windowSize - _count))
                    {
                        // Still not enough -> drop new packet
                        // Throttled logging
                        _dropLogCount++;
                        var now = DateTime.UtcNow;
                        bool shouldLog = (now - _lastDropLogTime).TotalMilliseconds > DROP_LOG_THROTTLE_MS || _dropLogCount >= DROP_LOG_THROTTLE_COUNT;
                        if (shouldLog)
                        {
                            _logger.LogWarning("ReorderQueue: dropping packet (still insufficient space after freeing) (seq={Seq}, begin={Begin}, end={End}, needed={Needed}, window={Window}, count={Count}, dropCount={DropCount})",
                                seq, _begin, end, needed, _windowSize, _count, _dropLogCount);
                            _lastDropLogTime = now;
                            _dropLogCount = 0;
                        }
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        return;
                    }
                }

                // Reserve intermediate slots then put the item
                ReserveSlotsUntil(seq);
                if (_buffer.TryGetValue(seq, out var entry))
                {
                    entry.Item = item;
                    entry.ArrivalTime = DateTime.UtcNow;
                }
                else
                {
                    // fallback (shouldn't happen)
                    _buffer[seq] = new QueueEntry { Item = item, ArrivalTime = DateTime.UtcNow };
                    _count++;
                }

                if (seq == _begin)
                    PullLockedLimited();
            }
        }

        /// <summary>
        /// Flush: if force==true push everything out immediately (used for shutdown/tests).
        /// Otherwise perform regular timeout based cleanup.
        /// </summary>
        public void Flush(bool force = false)
        {
            lock (_lock)
            {
                if (force)
                {
                    // Output everything present in order
                    while (PullLocked()) { }
                    return;
                }
                CheckTimeoutLocked();
            }
        }

        public (ulong processed, ulong dropped, ulong timeoutDropped, int bufferSize) GetStats()
        {
            lock (_lock)
            {
                int arrived = _buffer.Values.Count(e => e.IsSet);
                return (_totalProcessed, _totalDropped, _totalTimeoutDropped, arrived);
            }
        }

        public void SetDropStrategy(ReorderQueueDropStrategy strategy)
        {
            lock (_lock) { _dropStrategy = strategy; }
        }

        public void SetDropCallback(Action<T>? callback)
        {
            lock (_lock) { _dropCallback = callback; }
        }

        public void SetTimeoutCallback(Action? callback)
        {
            lock (_lock)
            {
                if (callback == null) return;
                if (_timeoutCallback != null)
                {
                    var old = _timeoutCallback;
                    _timeoutCallback = () => { old(); callback(); };
                }
                else
                {
                    _timeoutCallback = callback;
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _initialized = false;
                _begin = 0;
                _count = 0;
                _totalDropped = _totalProcessed = _totalTimeoutDropped = 0;
            }
        }

        #endregion

        #region Internal helpers (locked)

        // Place item into existing reserved slot or overwrite if already present
        private void PlaceIntoWindow(uint seq, T item)
        {
            if (_buffer.TryGetValue(seq, out var exist))
            {
                exist.Item = item;
                exist.ArrivalTime = DateTime.UtcNow;
            }
            else
            {
                _buffer[seq] = new QueueEntry { Item = item, ArrivalTime = DateTime.UtcNow };
                // NOTE: if we didn't reserve, this indicates a logic gap; ensure _count consistent by heuristics
                _count++;
            }
        }

        // Reserve intermediate slots between current end and seq (exclusive end)
        private void ReserveSlotsUntil(uint seq)
        {
            uint end = MaskSeq(_begin + (uint)_count);
            var now = DateTime.UtcNow;
            while (SequenceDistance(end, MaskSeq(seq + 1u)) > 0)
            {
                if (!_buffer.ContainsKey(end))
                {
                    _buffer[end] = new QueueEntry { Item = null, ArrivalTime = DateTime.MinValue, ReservedTime = now };
                }
                _count++;
                end = MaskSeq(_begin + (uint)_count);
            }
        }

        // Conservative free from begin: prefer removing non-set (holes) first, avoid removing set entries if possible
        private void FreeSpaceFromBeginConservatively(uint freeNeeded)
        {
            uint freed = 0;
            while (freed < freeNeeded && _count > 0)
            {
                // If head is not set, remove it immediately
                if (!_buffer.TryGetValue(_begin, out var headEntry) || !headEntry.IsSet)
                {
                    _buffer.Remove(_begin);
                    _begin = MaskSeq(_begin + 1u);
                    _count--;
                    freed++;
                    _totalDropped++;
                    _totalTimeoutDropped++;
                    continue;
                }

                // Head is set. If we must free more, be conservative: only drop non-key frames first.
                // Scan forward with dynamic lookahead based on backlog severity.
                bool foundToDrop = false;
                int arrivedCount = _buffer.Values.Count(e => e.IsSet);
                int lookahead;
                if (arrivedCount > 100)
                {
                    // Extreme backlog: aggressive lookahead, allow dropping keyframes if necessary
                    lookahead = Math.Min(64, _count);
                }
                else if (arrivedCount > 50)
                {
                    // High backlog: larger lookahead
                    lookahead = Math.Min(32, _count);
                }
                else
                {
                    // Normal: conservative lookahead
                    lookahead = Math.Min(16, _count);
                }
                uint scanSeq = _begin;
                for (int i = 0; i < lookahead; i++)
                {
                    if (!_buffer.TryGetValue(scanSeq, out var scanEntry) || !scanEntry.IsSet)
                    {
                        // hole -> remove
                        _buffer.Remove(scanSeq);
                        // logically need to shift window by count of removed before begin; to keep simple we will remove from begin until this slot
                        // remove from begin until scanSeq (inclusive)
                        uint removeCount = SequenceDistance(_begin, MaskSeq(scanSeq + 1u));
                        for (uint r = 0; r < removeCount && _count > 0; r++)
                        {
                            _buffer.Remove(_begin);
                            _begin = MaskSeq(_begin + 1u);
                            _count--;
                            freed++;
                            _totalDropped++;
                            _totalTimeoutDropped++;
                        }

                        foundToDrop = true;
                        break;
                    }
                    else if (_isKeyFrame != null && !_isKeyFrame(scanEntry.Item!))
                    {
                        // non-key frame: remove it to free space
                        // remove up to and including scanSeq
                        uint removeCount = SequenceDistance(_begin, MaskSeq(scanSeq + 1u));
                        for (uint r = 0; r < removeCount && _count > 0; r++)
                        {
                            _buffer.Remove(_begin);
                            _begin = MaskSeq(_begin + 1u);
                            _count--;
                            freed++;
                            _totalDropped++;
                        }
                        foundToDrop = true;
                        break;
                    }

                    scanSeq = MaskSeq(scanSeq + 1u);
                }

                if (!foundToDrop)
                {
                    // As last resort, drop the head (even if set) to free space
                    // In extreme backlog, allow dropping keyframes to prevent complete queue blocking
                    bool allowKeyframeDrop = arrivedCount > 100;
                    if (_buffer.TryGetValue(_begin, out var head))
                    {
                        bool isKey = _isKeyFrame != null && _isKeyFrame(head.Item!);
                        if (!isKey || allowKeyframeDrop)
                        {
                            _buffer.Remove(_begin);
                            _begin = MaskSeq(_begin + 1u);
                            _count--;
                            freed++;
                            _totalDropped++;
                            if (isKey && allowKeyframeDrop)
                            {
                                _logger.LogWarning("ReorderQueue: forced drop of keyframe head seq to free space (extreme backlog)");
                            }
                        }
                        else
                        {
                            // Can't drop keyframe, try next slot
                            uint nextSeq = MaskSeq(_begin + 1u);
                            if (_buffer.TryGetValue(nextSeq, out var nextEntry) && nextEntry.IsSet)
                            {
                                _buffer.Remove(nextSeq);
                                _begin = MaskSeq(nextSeq + 1u);
                                _count--;
                                freed++;
                                _totalDropped++;
                            }
                        }
                    }
                }
            }
        }

        // Pull with dynamic limit based on backlog to avoid window accumulation
        private void PullLockedLimited()
        {
            // Dynamic output limit: increase when backlog is high
            int dynamicMax = _maxOutputPerPull;
            if (_count > 100)
            {
                // High backlog: allow more outputs to catch up
                dynamicMax = Math.Min(MAX_OUTPUT_PER_PULL, Math.Max(_maxOutputPerPull, _count / 10));
            }
            else if (_count > 50)
            {
                // Medium backlog: moderate increase
                dynamicMax = Math.Min(MAX_OUTPUT_PER_PULL, _maxOutputPerPull * 2);
            }

            int outputs = 0;
            while (outputs < dynamicMax && PullLocked())
            {
                outputs++;
            }
        }

        // Pull all available (used by force flush)
        private bool PullLocked()
        {
            if (_count == 0) return false;
            bool pulledAny = false;
            while (_count > 0)
            {
                if (!_buffer.TryGetValue(_begin, out var entry))
                    break; // sparse
                if (!entry.IsSet)
                    break; // head not arrived

                _outputCallback(entry.Item!);
                _buffer.Remove(_begin);
                _begin = MaskSeq(_begin + 1u);
                _count--;
                _totalProcessed++;
                pulledAny = true;
            }
            return pulledAny;
        }

        private void CheckTimeoutLocked()
        {
            if (_count == 0) return;
            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();

            int arrivedCount = _buffer.Values.Count(e => e.IsSet);
            // ⚠️ 优化：降低积压阈值，更早触发清理（从50降到30）
            bool isHeavyBacklog = arrivedCount > 30;
            
            // ⚠️ 优化：考虑窗口使用率，而不仅仅是已到达包的数量
            double windowUsageRate = _windowSize > 0 ? (double)_count / _windowSize : 0;
            bool isHighWindowUsage = windowUsageRate > 0.8; // 窗口使用率超过80%
            
            int maxConsecutive = 8;
            if (isHeavyBacklog || isHighWindowUsage)
            {
                if (arrivedCount > 500 || windowUsageRate > 0.95)
                {
                    maxConsecutive = 100; // 极端积压或窗口几乎满
                }
                else if (arrivedCount > 300 || windowUsageRate > 0.9)
                {
                    maxConsecutive = 80; // 严重积压
                }
                else if (arrivedCount > 150 || windowUsageRate > 0.85)
                {
                    maxConsecutive = 50; // 中度积压
                }
                else if (arrivedCount > 60)
                {
                    maxConsecutive = 30; // 一般积压
                }
                else
                {
                    maxConsecutive = 20; // 轻微积压但窗口使用率高
                }
            }
            else if (arrivedCount > 20)
            {
                maxConsecutive = 10;
            }
            else if (arrivedCount > 15)
            {
                maxConsecutive = 8;
            }

            // We only consider timeout for consecutive slots starting from begin (conservative)
            uint seq = _begin;
            int consecutive = 0;

            for (int i = 0; i < _count && consecutive < maxConsecutive; i++)
            {
                if (!_buffer.TryGetValue(seq, out var entry))
                {
                    // hole -> treat as timeout candidate
                    toRemove.Add(seq);
                    consecutive++;
                    seq = MaskSeq(seq + 1u);
                    continue;
                }

                bool isTimeout = false;
                if (!entry.IsSet)
                {
                    if (entry.ReservedTime != DateTime.MinValue)
                    {
                        var elapsed = (now - entry.ReservedTime).TotalMilliseconds;
                        double timeoutThreshold = _timeoutMs * 2.0; // 默认值
                        
                        if (isHeavyBacklog || isHighWindowUsage)
                        {
                            if (arrivedCount > 500 || windowUsageRate > 0.95)
                            {
                                timeoutThreshold = _timeoutMs * 0.5; // 极端积压：大幅缩短
                            }
                            else if (arrivedCount > 300 || windowUsageRate > 0.9)
                            {
                                timeoutThreshold = _timeoutMs * 0.7; // 严重积压
                            }
                            else if (arrivedCount > 150 || windowUsageRate > 0.85)
                            {
                                timeoutThreshold = _timeoutMs * 1.0; // 中度积压
                            }
                            else
                            {
                                timeoutThreshold = _timeoutMs * 1.2; // 一般积压
                            }
                        }
                        
                        if (elapsed > timeoutThreshold)
                            isTimeout = true;
                    }
                    else
                    {
                        isTimeout = true; // no reserved time means it's stale
                    }
                }
                else
                {
                    var elapsed = (now - entry.ArrivalTime).TotalMilliseconds;
                    double timeoutThreshold = _timeoutMs; // 默认值
                    
                    if (isHeavyBacklog || isHighWindowUsage)
                    {
                        if (arrivedCount > 500 || windowUsageRate > 0.95)
                        {
                            timeoutThreshold = _timeoutMs * 0.3; // 极端积压：大幅加速输出
                        }
                        else if (arrivedCount > 300 || windowUsageRate > 0.9)
                        {
                            timeoutThreshold = _timeoutMs * 0.5; // 严重积压
                        }
                        else if (arrivedCount > 150 || windowUsageRate > 0.85)
                        {
                            timeoutThreshold = _timeoutMs * 0.7; // 中度积压
                        }
                        else
                        {
                            timeoutThreshold = _timeoutMs * 0.8; // 一般积压
                        }
                    }
                    
                    if (elapsed > timeoutThreshold)
                        isTimeout = true;
                }

                if (isTimeout)
                {
                    if (entry.IsSet)
                    {
                        // output arrived item rather than drop (keep continuity)
                        _outputCallback(entry.Item!);
                        _totalProcessed++;
                    }
                    else
                    {
                        _totalDropped++;
                        _totalTimeoutDropped++;
                    }
                    toRemove.Add(seq);
                    consecutive++;
                    seq = MaskSeq(seq + 1u);
                }
                else
                {
                    // hit a slot that isn't timed out -> stop conservative timeout sweep
                    break;
                }
            }

            if (toRemove.Count == 0) return;

            foreach (var s in toRemove)
                _buffer.Remove(s);

            _begin = MaskSeq(_begin + (uint)toRemove.Count);
            _count -= toRemove.Count;

            _timeoutCallback?.Invoke();
            PullLockedLimited();
        }

        #endregion

        #region Sequence helpers

        private static uint SequenceDistance(uint from, uint to)
        {
            return (to - from) & SEQ_MASK;
        }

        private static uint MaskSeq(uint value) => value & SEQ_MASK;

        // ✅ 原则1：使用严格的数学定义判断seq是否比base"新"
        // seq 比 base "新" <==> (seq - base) mod 65536 < 32768
        // seq 比 base "旧" <==> (seq - base) mod 65536 >= 32768
        private static bool IsNewer(uint seq, uint @base)
        {
            return ((seq - @base) & SEQ_MASK) < HALF_SPACE;
        }

        // true if a is strictly less than b in modular arithmetic (< with half-space cutoff)
        // ⚠️ 保留用于向后兼容，但新代码应该使用 IsNewer
        private static bool SeqLess(uint a, uint b)
        {
            return SequenceDistance(a, b) != 0 && SequenceDistance(a, b) < HALF_SPACE;
        }

        private bool SeqInWindow(uint seq, uint windowBegin, uint windowCount)
        {
            if (windowCount == 0) return false;
            uint dist = SequenceDistance(windowBegin, seq);
            return dist < (uint)windowCount;
        }

        // ✅ 已废弃：不再需要特殊检测回绕
        // 回绕是自然发生的，IsNewer会自动正确处理
        // 保留此方法以保持向后兼容性，但实际上不会被调用
        [Obsolete("Use IsNewer instead. Wrap-around is handled naturally.")]
        private static bool IsLikelyWrapAround(uint seq, uint begin)
        {
            // 这个方法不再使用，但保留以避免编译错误
            return false;
        }

        #endregion

        #region Nested types

        private class QueueEntry
        {
            public T? Item { get; set; }
            public DateTime ArrivalTime { get; set; } = DateTime.MinValue;
            public DateTime ReservedTime { get; set; } = DateTime.MinValue;
            public bool IsSet => Item != null;
        }

        #endregion
    }
}
