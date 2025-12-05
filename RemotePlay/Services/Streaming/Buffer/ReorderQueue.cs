using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// 安全且适用于 16-bit 回环序列号（0..65535）的重排序队列实现。
    /// 特性：
    /// - 不使用按数值排序的容器（避免回环问题）。使用 Dictionary + 逻辑顺序推进。
    /// - 明确的序列比较（模 65536）函数，避免错误判断。
    /// - 可配置的窗口大小、超时、丢弃策略与回调。
    /// - 线程安全（内部 lock），并尽量减少锁内工作量。
    /// - 友好的统计信息，Flush/Reset 支持。
    ///
    /// 设计要点：
    /// - 序列比较使用 (to - from) & 0xFFFF 的距离判断。
    /// - _buffer 只保存存在的 slot（已收到或预留），并用 _begin/_count 管理逻辑窗口。
    /// - 不依赖 SortedDictionary，因此回环时顺序正确。
    ///
    /// </summary>
    public enum ReorderQueueDropStrategy
    {
        /// <summary>当空间不足时，丢弃新到达的包（默认行为）</summary>
        End,
        /// <summary>当空间不足时，优先丢弃最早（队首）未准备好的 slot</summary>
        Begin
    }

    public class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFFu;
        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeqNum;
        private readonly Action<T> _outputCallback;
        private Action<T>? _dropCallback;
        private Action? _timeoutCallback;

        // 用 Dictionary 保存 slot（key = seq），通过 _begin/_count 管理逻辑窗口
        private readonly Dictionary<uint, QueueEntry> _buffer = new Dictionary<uint, QueueEntry>();
        private readonly object _lock = new object();

        private uint _begin = 0; // 下一个期望输出的序列号
        private int _count = 0;  // 当前窗口内 slot 总数（包括已收到与预留）
        private int _windowSize; // 当前窗口大小（capacity）
        private readonly int _sizeMin;
        private readonly int _sizeMax;
        private readonly int _timeoutMs;
        private ReorderQueueDropStrategy _dropStrategy;

        private bool _initialized = false;

        private ulong _totalProcessed = 0;
        private ulong _totalDropped = 0;
        private ulong _totalTimeoutDropped = 0;
        
        // ✅ 重置后的宽限期：重置后的一段时间内，更宽松地接受包
        private DateTime _lastResetTime = DateTime.MinValue;
        private const int RESET_GRACE_PERIOD_MS = 1000; // ✅ 减少宽限期到1秒，避免过于宽松导致帧率波动
        
        // ✅ 窗口恢复限制：避免过于频繁的窗口重置
        private DateTime _lastWindowResetTime = DateTime.MinValue;
        private const int MIN_WINDOW_RESET_INTERVAL_MS = 500; // 最小窗口重置间隔500ms，避免频繁重置导致帧率波动

        /// <summary>
        /// 构造函数
        /// </summary>
        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            Action<T>? dropCallback = null,
            int sizeStart = 32,
            int sizeMin = 8,
            int sizeMax = 128,
            int timeoutMs = 80,
            ReorderQueueDropStrategy dropStrategy = ReorderQueueDropStrategy.End,
            Action? timeoutCallback = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _getSeqNum = getSeqNum ?? throw new ArgumentNullException(nameof(getSeqNum));
            _outputCallback = outputCallback ?? throw new ArgumentNullException(nameof(outputCallback));
            _dropCallback = dropCallback;
            _timeoutCallback = timeoutCallback;

            _sizeMin = Math.Max(4, sizeMin);
            _sizeMax = Math.Max(_sizeMin, sizeMax);
            _windowSize = Math.Clamp(sizeStart, _sizeMin, _sizeMax);
            _timeoutMs = Math.Max(10, timeoutMs);
            _dropStrategy = dropStrategy;
        }

        #region Public API

        /// <summary>
        /// 推入一个包到队列。线程安全。
        /// </summary>
        public void Push(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            lock (_lock)
            {
                uint seq = MaskSeq(_getSeqNum(item));

                // 第一次到达：直接输出，初始化 _begin
                if (!_initialized)
                {
                    _initialized = true;
                    _begin = MaskSeq(seq + 1);
                    _outputCallback(item);
                    _totalProcessed++;
                    return;
                }

                // 如果 seq 在 [begin, begin+count) 内（窗口内部）
                uint end = MaskSeq(_begin + (uint)_count);

                if (SeqInWindow(seq, _begin, (uint)_count))
                {
                    // 已存在 slot？覆盖 arrival
                    if (_buffer.TryGetValue(seq, out var exist))
                    {
                        if (exist.IsSet)
                        {
                            // 重复包：如果是已经输出的 begin（理论上不会），尝试 Pull
                            // 否则直接 drop
                            _dropCallback?.Invoke(item);
                            _totalDropped++;
                            return;
                        }
                        exist.Item = item;
                        exist.ArrivalTime = DateTime.UtcNow;
                    }
                    else
                    {
                        _buffer[seq] = new QueueEntry { Item = item, ArrivalTime = DateTime.UtcNow };
                    }

                    // 如果是队首，推进输出
                    if (seq == _begin)
                        PullLocked();

                    return;
                }

                // seq 在 begin 之前：检查是否可能是窗口跳过或重置后的包
                if (SeqLess(seq, _begin))
                {
                    // ✅ 检查是否在重置后的宽限期内
                    bool inResetGracePeriod = _lastResetTime != DateTime.MinValue && 
                                             (DateTime.UtcNow - _lastResetTime).TotalMilliseconds < RESET_GRACE_PERIOD_MS;
                    
                    // ✅ 改进：检查距离_begin有多远
                    uint distance = SequenceDistance(seq, _begin);
                    const uint MAX_RECOVERY_DISTANCE = 2048; // ✅ 减小恢复距离到2048，避免过于激进的恢复导致帧率波动
                    
                    // ✅ 关键改进：如果窗口为空，或者未初始化，总是接受包（重置后窗口可能为空）
                    if (_count == 0 || !_initialized)
                    {
                        // ✅ 窗口为空或未初始化，直接接受这个包，重新初始化窗口
                        _logger.LogWarning("ReorderQueue: window empty/not initialized, accepting packet and resetting window. seq={Seq}, begin={Begin}, initialized={Init}", 
                            seq, _begin, _initialized);
                        _buffer.Clear();
                        _initialized = true;
                        _begin = MaskSeq(seq + 1);
                        _count = 0;
                        _lastWindowResetTime = DateTime.UtcNow; // ✅ 记录窗口重置时间
                        _outputCallback(item);
                        _totalProcessed++;
                        return;
                    }
                    
                    // ✅ 如果在重置宽限期内，优先恢复窗口
                    if (inResetGracePeriod)
                    {
                        // ✅ 重置宽限期内，接受包，但仅在距离不太远时（避免接受太老的包导致帧率波动）
                        if (distance < MAX_RECOVERY_DISTANCE)
                        {
                            _logger.LogWarning("ReorderQueue: accepting packet in reset grace period. seq={Seq}, begin={Begin}, distance={Distance}", 
                                seq, _begin, distance);
                            _buffer.Clear();
                            _initialized = true;
                            _begin = MaskSeq(seq + 1);
                            _count = 0;
                            _outputCallback(item);
                            _totalProcessed++;
                            return;
                        }
                        else
                        {
                            // 距离太远，即使是在宽限期内也不接受，避免帧率波动
                            _logger.LogDebug("ReorderQueue: packet too far in grace period, dropping. seq={Seq}, begin={Begin}, distance={Distance}", 
                                seq, _begin, distance);
                        }
                    }
                    
                    // ✅ 距离不远，可能是窗口跳过，尝试恢复（但仅在距离较近时）
                    // ✅ 检查窗口重置间隔，避免过于频繁的重置导致帧率波动
                    if (distance < MAX_RECOVERY_DISTANCE)
                    {
                        // 检查距离上次窗口重置是否已经过了足够的时间
                        bool canResetWindow = _lastWindowResetTime == DateTime.MinValue || 
                                            (DateTime.UtcNow - _lastWindowResetTime).TotalMilliseconds >= MIN_WINDOW_RESET_INTERVAL_MS;
                        
                        if (canResetWindow)
                        {
                            // 距离不远，可能是窗口跳过，尝试恢复
                            _logger.LogWarning("ReorderQueue: window may have skipped, resetting window. seq={Seq}, begin={Begin}, distance={Distance}, count={Count}", 
                                seq, _begin, distance, _count);
                            
                            // ✅ 重置窗口，从这个包重新开始
                            _buffer.Clear();
                            _initialized = true;
                            _begin = MaskSeq(seq + 1);
                            _count = 0;
                            _lastWindowResetTime = DateTime.UtcNow; // ✅ 记录窗口重置时间
                            // ✅ 不清除重置时间，保持宽限期，直到窗口恢复正常
                            _outputCallback(item);
                            _totalProcessed++;
                            return;
                        }
                        else
                        {
                            // 窗口重置过于频繁，丢弃这个包以避免帧率波动
                            _logger.LogDebug("ReorderQueue: window reset too frequent, dropping packet to avoid frame rate fluctuation. seq={Seq}, begin={Begin}, distance={Distance}", 
                                seq, _begin, distance);
                            _dropCallback?.Invoke(item);
                            _totalDropped++;
                            return;
                        }
                    }
                    
                    // ✅ 距离很远，但检查是否在回环窗口的另一侧（可能是seq号回环）
                    // 如果距离超过32768，可能是seq号回环导致的，应该接受
                    if (distance > 0x8000u)
                    {
                        // 在回环的另一侧，实际上是"新包"，重置窗口接受它
                        _logger.LogWarning("ReorderQueue: seq appears to be on wrap-around side, resetting window. seq={Seq}, begin={Begin}, distance={Distance}", 
                            seq, _begin, distance);
                        _buffer.Clear();
                        _initialized = true;
                        _begin = MaskSeq(seq + 1);
                        _count = 0;
                        _lastResetTime = DateTime.UtcNow; // ✅ 重置后设置重置时间
                        _outputCallback(item);
                        _totalProcessed++;
                        return;
                    }
                    
                    // 否则，确实是老包，丢弃
                    _dropCallback?.Invoke(item);
                    _totalDropped++;
                    return;
                }

                // seq 在窗口外（未来太远），需要扩展窗口或丢弃
                // 计算当前可用空位
                uint free = (uint)(_windowSize - _count);
                uint newEnd = MaskSeq(seq + 1);
                uint needed = SequenceDistance(end, newEnd); // 需要扩展多少 slot

                if (needed > free)
                {
                    // 空间不足
                    if (_dropStrategy == ReorderQueueDropStrategy.End)
                    {
                        // 丢弃新到达的包
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        return;
                    }

                    // 否则，丢弃/处理队首直到有空间
                    while (needed > 0 && _count > 0)
                    {
                        // 如果队首存在且已就绪，则输出以回收空间
                        if (_buffer.TryGetValue(_begin, out var headEntry) && headEntry.IsSet)
                        {
                            _outputCallback(headEntry.Item!);
                            _totalProcessed++;
                        }
                        else
                        {
                            // 若队首未到达，则视为丢失（timeout dropped）
                            _totalDropped++;
                            _totalTimeoutDropped++;
                        }

                        _buffer.Remove(_begin);
                        _begin = MaskSeq(_begin + 1);
                        _count--;
                        free = (uint)(_windowSize - _count);
                        // 重新计算 end 和 needed（end 由 _begin/_count 定义）
                        end = MaskSeq(_begin + (uint)_count);
                        newEnd = MaskSeq(seq + 1);
                        needed = SequenceDistance(end, newEnd);
                    }

                    // 如果仍然不足空间，最终丢弃新包
                    if (needed > 0)
                    {
                        _dropCallback?.Invoke(item);
                        _totalDropped++;
                        return;
                    }
                }

                // 现在预留中间的 slot
                end = MaskSeq(_begin + (uint)_count);
                var now = DateTime.UtcNow;
                while (SequenceDistance(end, MaskSeq(seq + 1)) > 0)
                {
                    if (!_buffer.ContainsKey(end))
                    {
                        _buffer[end] = new QueueEntry { Item = null, ArrivalTime = DateTime.MinValue, ReservedTime = now };
                    }
                    _count++;
                    end = MaskSeq(_begin + (uint)_count);
                }

                // 放入实际 item
                if (_buffer.TryGetValue(seq, out var entry))
                {
                    entry.Item = item;
                    entry.ArrivalTime = DateTime.UtcNow;
                }
                else
                {
                    // 理论上上面已预留，但是兜底
                    _buffer[seq] = new QueueEntry { Item = item, ArrivalTime = DateTime.UtcNow };
                    _count++;
                }

                if (seq == _begin)
                    PullLocked();
            }
        }

        /// <summary>
        /// 检查并清理超时、可强制 flush 或触发超时回调
        /// </summary>
        public void Flush(bool force = false)
        {
            lock (_lock)
            {
                if (force)
                {
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
                _lastResetTime = DateTime.UtcNow; // ✅ 记录重置时间，用于宽限期判断
            }
        }

        #endregion

        #region Internal helpers (locked)

        // 假设已在 _lock 内调用
        private bool PullLocked()
        {
            // 如果没有任何 slot
            if (_count == 0) return false;

            bool pulledAny = false;
            // 连续输出已就绪的 slot（限制单次输出数量可在这里加入）
            while (_count > 0)
            {
                if (!_buffer.TryGetValue(_begin, out var entry))
                    break; // 没有 slot（稀疏）

                if (!entry.IsSet)
                    break; // 队首未到达

                _outputCallback(entry.Item!);
                _buffer.Remove(_begin);
                _begin = MaskSeq(_begin + 1);
                _count--;
                _totalProcessed++;
                pulledAny = true;
            }

            return pulledAny;
        }

        // 假设已在 _lock 内调用
        private void CheckTimeoutLocked()
        {
            if (_count == 0) return;

            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();
            uint seq = _begin;
            int consecutiveTimeouts = 0;
            int maxConsecutiveTimeouts = 5;
            
            // 检测队列积压情况，动态调整恢复策略
            int arrivedCount = _buffer.Values.Count(e => e.IsSet);
            bool isHeavyBacklog = arrivedCount > 35;
            
            if (isHeavyBacklog)
            {
                // 游戏串流优化：队列积压时快速恢复，保持低延迟
                maxConsecutiveTimeouts = 10;
                _logger.LogWarning("ReorderQueue: 检测到严重积压 (arrivedCount={Arrived}, bufferSize={BufferSize}), 启用快速恢复模式 (maxSkip={MaxSkip})", 
                    arrivedCount, _count, maxConsecutiveTimeouts);
            }
            else if (arrivedCount > 20)
            {
                // 中度积压，适度提高恢复速度
                maxConsecutiveTimeouts = 7;
            }

            for (int i = 0; i < _count && consecutiveTimeouts < maxConsecutiveTimeouts; i++)
            {
                if (!_buffer.TryGetValue(seq, out var entry))
                {
                    // 若 slot 本身都不存在，视为未到达，计为超时并继续
                    toRemove.Add(seq);
                    consecutiveTimeouts++;
                    seq = MaskSeq(seq + 1);
                    continue;
                }

                bool isTimeout = false;
                if (!entry.IsSet)
                {
                    // 未收到，使用 ReservedTime 判断
                    if (entry.ReservedTime != DateTime.MinValue)
                    {
                        var elapsed = (now - entry.ReservedTime).TotalMilliseconds;
                        double timeoutThreshold = _timeoutMs * 2;
                        
                        // 如果队列积压严重，适度缩短等待时间，但不要太激进
                        if (isHeavyBacklog)
                        {
                            timeoutThreshold = _timeoutMs * 1.5;
                        }
                        
                        if (elapsed > timeoutThreshold)
                            isTimeout = true;
                    }
                    else
                    {
                        // 兜底：没有 reservedTime 视为可立即处理
                        isTimeout = true;
                    }
                }
                else
                {
                    var elapsed = (now - entry.ArrivalTime).TotalMilliseconds;
                    double timeoutThreshold = _timeoutMs;
                    
                    // 如果队列积压严重且这个包已经等待很久，优先输出
                    // 但不要太激进，保持画面连续性
                    if (isHeavyBacklog)
                    {
                        timeoutThreshold = _timeoutMs * 0.8;
                    }
                    
                    if (elapsed > timeoutThreshold)
                        isTimeout = true;
                }

                if (isTimeout)
                {
                    if (entry.IsSet)
                    {
                        // 已收到但过时，优先输出（保持连续）
                        _outputCallback(entry.Item!);
                        _totalProcessed++;
                    }
                    else
                    {
                        // 从未到达
                        _totalDropped++;
                        _totalTimeoutDropped++;
                    }

                    toRemove.Add(seq);
                    consecutiveTimeouts++;
                    seq = MaskSeq(seq + 1);
                }
                else
                {
                    // 遇到未超时 slot 停止（避免跳过仍有机会到达的包）
                    // 即使积压严重，也要给包足够的时间到达，保持画面连续性
                    break;
                }
            }

            if (toRemove.Count == 0) return;

            // 记录日志（合并记录）
            if (toRemove.Count > 5)
            {
                _logger.LogWarning("ReorderQueue: timeout batch removed {Count} slots from {From} to {To}",
                    toRemove.Count, toRemove.First(), toRemove.Last());
            }
            else
            {
                // 如果有未收到的 slot，记录其序号
                var firstUnreceived = toRemove.FirstOrDefault(s => !_buffer.ContainsKey(s) || !_buffer[s].IsSet);
                if (firstUnreceived != 0 || toRemove.Contains(0u)) // 注意 0 可能是真正的 seq
                {
                    _logger.LogDebug("ReorderQueue: timeout skip starting at {Seq}", toRemove[0]);
                }
            }

            // 移除并推进 _begin / _count
            foreach (var s in toRemove)
            {
                _buffer.Remove(s);
            }

            _begin = MaskSeq(_begin + (uint)toRemove.Count);
            _count -= toRemove.Count;

            // 调用超时回调
            _timeoutCallback?.Invoke();

            // 尝试继续输出后续已就绪的包
            PullLocked();
        }

        #endregion

        #region Sequence helpers

        /// <summary>
        /// 返回 (to - from) & 0xFFFF
        /// </summary>
        private static uint SequenceDistance(uint from, uint to)
        {
            return (to - from) & SEQ_MASK;
        }

        private static uint MaskSeq(uint value) => value & SEQ_MASK;

        /// <summary>
        /// 判断 a < b（模 65536 逻辑）
        /// 返回 true 当且仅当 b 在 a 的 "后面" 少于 32768
        /// </summary>
        private static bool SeqLess(uint a, uint b)
        {
            return SequenceDistance(a, b) != 0 && SequenceDistance(a, b) < 0x8000u;
        }

        private static bool SeqLessOrEqual(uint a, uint b)
        {
            return SequenceDistance(a, b) < 0x8000u;
        }

        private bool SeqInWindow(uint seq, uint windowBegin, uint windowCount)
        {
            if (windowCount == 0) return false;
            uint dist = SequenceDistance(windowBegin, seq);
            return dist < (uint)windowCount;
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
