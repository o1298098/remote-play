using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    public enum ReorderQueueDropStrategy
    {
        End,
        Begin
    }

    public sealed class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFFu;
        private const uint HALF_SPACE = 0x8000u;

        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeq;
        private readonly Action<T> _output;
        private Action<T>? _drop;
        private Action? _timeoutCallback;
        private readonly Func<T, bool>? _isKeyFrame;

        private Slot[] _slots;
        private int _capacity;
        private int _capacityMask;

        private uint _baseSeq;
        private int _countSlots;
        private int _arrived;

        private readonly int _sizeMin;
        private readonly int _sizeMax;
        private readonly int _timeoutMs;
        private ReorderQueueDropStrategy _dropStrategy;

        private bool _initialized;

        private ulong _processed;
        private ulong _dropped;
        private ulong _timeoutDropped;

        private DateTime _lastDropLog = DateTime.MinValue;
        private int _dropLogCount = 0;
        private const int DROP_LOG_THROTTLE_MS = 1000;
        private const int DROP_LOG_THROTTLE_COUNT = 50;

        private readonly int _maxOutputPerPull;
        private const int DEFAULT_MAX_OUTPUT_PER_PULL = 3;
        private const int MAX_OUTPUT_PER_PULL = 20;

        private const int CLEANUP_SCAN_LIMIT = 32;
        private const uint MAX_RESET_GAP_FACTOR = 2;

        private readonly object _lock = new object();

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
            _getSeq = getSeqNum ?? throw new ArgumentNullException(nameof(getSeqNum));
            _output = outputCallback ?? throw new ArgumentNullException(nameof(outputCallback));
            _drop = dropCallback;
            _isKeyFrame = isKeyFrame;
            _timeoutCallback = timeoutCallback;

            _sizeMin = Math.Max(4, sizeMin);
            _sizeMax = Math.Max(_sizeMin, sizeMax);
            int start = Math.Clamp(sizeStart, _sizeMin, _sizeMax);
            _capacity = NextPowerOfTwo(start);
            _capacityMask = _capacity - 1;
            _slots = new Slot[_capacity];

            _timeoutMs = Math.Max(10, timeoutMs);
            _dropStrategy = dropStrategy;
            _maxOutputPerPull = Math.Max(1, Math.Min(MAX_OUTPUT_PER_PULL, maxOutputPerPull));
        }

        #region Public API

        public void Push(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            lock (_lock)
            {
                uint seq = MaskSeq(_getSeq(item));

                if (!_initialized)
                {
                    Initialize(seq, item);
                    return;
                }

                if (CheckDeadLock(seq, item))
                    return;

                uint dist = SequenceDistance(_baseSeq, seq);
                if (dist < (uint)_countSlots)
                {
                    PutInSlot(seq, item);
                    if (seq == _baseSeq)
                        PullLockedLimited();
                    return;
                }

                if (!IsNewer(seq, _baseSeq))
                {
                    uint gap = SequenceDistance(_baseSeq, seq);
                    bool likelyWrap = _baseSeq > HALF_SPACE && seq < HALF_SPACE && gap > HALF_SPACE;
                    if (likelyWrap)
                    {
                        ResetWindowTo(seq, item, "wrap-around");
                        return;
                    }

                    LogDropThrottled(seq, $"old packet (gap={gap})");
                    _drop?.Invoke(item);
                    _dropped++;
                    return;
                }

                uint endSeq = MaskSeq(_baseSeq + (uint)_countSlots);
                uint needed = SequenceDistance(endSeq, MaskSeq(seq + 1u));

                if (needed > (uint)_sizeMax * MAX_RESET_GAP_FACTOR)
                {
                    ResetWindowTo(seq, item, "excessive gap");
                    return;
                }

                if (!EnsureSpaceFor(needed))
                {
                    LogDropThrottled(seq, "insufficient space");
                    _drop?.Invoke(item);
                    _dropped++;
                    return;
                }

                ReserveSlotsUntil(seq);
                PutInSlot(seq, item);
                if (seq == _baseSeq)
                    PullLockedLimited();
            }
        }

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
                return (_processed, _dropped, _timeoutDropped, _arrived);
            }
        }

        public void SetDropStrategy(ReorderQueueDropStrategy strategy)
        {
            lock (_lock) { _dropStrategy = strategy; }
        }

        public void SetDropCallback(Action<T>? cb)
        {
            lock (_lock) { _drop = cb; }
        }

        public void SetTimeoutCallback(Action? cb)
        {
            lock (_lock)
            {
                if (cb == null) return;
                if (_timeoutCallback != null)
                {
                    var old = _timeoutCallback;
                    _timeoutCallback = () => { old(); cb(); };
                }
                else
                {
                    _timeoutCallback = cb;
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                ClearAllSlots();
                _initialized = false;
                _baseSeq = 0;
                _countSlots = 0;
                _arrived = 0;
                _processed = _dropped = _timeoutDropped = 0;
            }
        }

        #endregion

        #region Internal core

        private void Initialize(uint seq, T item)
        {
            _baseSeq = seq;
            ClearAllSlots();
            EnsureCapacity(1);
            int idx = IndexForOffset(0);
            _slots[idx].Set(seq, item, DateTime.UtcNow);
            _countSlots = 1;
            _arrived = 1;
            _initialized = true;
            PullLockedLimited();
        }

        private bool CheckDeadLock(uint seq, T item)
        {
            if (_countSlots > 0 && _arrived == 0)
            {
                _logger.LogWarning("ReorderQueue: detected dead window (all slots empty), resetting. begin={Begin}, seq={Seq}, count={Count}",
                    _baseSeq, seq, _countSlots);
                ResetWindowTo(seq, item, "dead window");
                return true;
            }
            return false;
        }

        private void ResetWindowTo(uint seq, T item, string reason)
        {
            _logger.LogWarning("ReorderQueue: resetting window ({Reason}) begin={Begin} -> seq={Seq}", reason, _baseSeq, seq);
            ClearAllSlots();
            EnsureCapacity(1);
            _baseSeq = seq;
            int idx = IndexForOffset(0);
            _slots[idx].Set(seq, item, DateTime.UtcNow);
            _countSlots = 1;
            _arrived = 1;
            _processed++;
            PullLockedLimited();
        }

        private bool EnsureSpaceFor(uint needed)
        {
            if (_countSlots + needed > (uint)_sizeMax)
                return false;

            int free = _capacity - _countSlots;
            if (needed <= (uint)free)
                return true;

            if (TryExpandToFit(needed))
            {
                free = _capacity - _countSlots;
                if (needed <= (uint)free) return true;
            }

            if (TryCleanupHeadFast())
            {
                free = _capacity - _countSlots;
                if (needed <= (uint)free) return true;
            }

            if (_dropStrategy == ReorderQueueDropStrategy.Begin)
            {
                if (FreeFromBegin(needed - (uint)free))
                {
                    free = _capacity - _countSlots;
                    if (needed <= (uint)free) return true;
                }
            }

            return false;
        }

        private bool TryExpandToFit(uint needed)
        {
            if (needed > (uint)_sizeMax) return false;

            int required = (int)(_countSlots + needed);
            int newCap = NextPowerOfTwo(Math.Clamp(required, _sizeMin, _sizeMax));
            if (newCap > _capacity)
            {
                var newSlots = new Slot[newCap];
                int newMask = newCap - 1;

                for (int i = 0; i < _countSlots; i++)
                {
                    int oldIdx = IndexForOffset(i);
                    int newIdx = (int)((_baseSeq + (uint)i) & (uint)newMask);
                    newSlots[newIdx] = _slots[oldIdx];
                }

                _slots = newSlots;
                _capacity = newCap;
                _capacityMask = newMask;
                _logger.LogDebug("ReorderQueue: expanded capacity to {Cap}", _capacity);
                return true;
            }
            return false;
        }

        private bool TryCleanupHeadFast()
        {
            if (_countSlots == 0) return false;
            var now = DateTime.UtcNow;
            int cleaned = 0;
            int scan = Math.Min(CLEANUP_SCAN_LIMIT, _countSlots);

            for (int i = 0; i < scan; i++)
            {
                int idx = IndexForOffset(i);
                ref Slot s = ref _slots[idx];

                if (!s.Exists)
                {
                    cleaned++;
                    continue;
                }

                if (!s.Occupied)
                {
                    cleaned++;
                    continue;
                }

                var elapsed = (now - s.ArrivalTime).TotalMilliseconds;
                if (elapsed > _timeoutMs)
                {
                    _output(s.Item!);
                    s.Clear();
                    _processed++;
                    _arrived--;
                    cleaned++;
                    continue;
                }

                break;
            }

            if (cleaned > 0)
            {
                AdvanceBaseBy(cleaned);
                _timeoutCallback?.Invoke();
                return true;
            }
            return false;
        }

        private bool FreeFromBegin(uint need)
        {
            if (need == 0) return true;
            uint freed = 0;
            int tries = 0;

            while (freed < need && _countSlots > 0 && tries++ < _capacity)
            {
                int idx = IndexForOffset(0);
                ref Slot s = ref _slots[idx];

                if (!s.Exists)
                {
                    s.Clear();
                    AdvanceBaseBy(1);
                    freed++;
                    _dropped++;
                    _timeoutDropped++;
                    continue;
                }

                if (!s.Occupied)
                {
                    s.Clear();
                    AdvanceBaseBy(1);
                    freed++;
                    _dropped++;
                    _timeoutDropped++;
                    continue;
                }

                if (_isKeyFrame != null && _isKeyFrame(s.Item!) && _arrived < 100)
                    break;

                _output(s.Item!);
                s.Clear();
                _processed++;
                _arrived--;
                AdvanceBaseBy(1);
                freed++;
            }

            return freed > 0;
        }

        private void ReserveSlotsUntil(uint seq)
        {
            uint endSeq = MaskSeq(_baseSeq + (uint)_countSlots);
            uint needed = SequenceDistance(endSeq, MaskSeq(seq + 1u));

            for (uint i = 0; i < needed; i++)
            {
                int idx = IndexForOffset(_countSlots);
                if (!_slots[idx].Exists)
                {
                    _slots[idx].Reserve(DateTime.UtcNow);
                }
                _countSlots++;
            }
        }

        private void PutInSlot(uint seq, T item)
        {
            uint offset = SequenceDistance(_baseSeq, seq);
            if (offset >= (uint)_countSlots)
            {
                ReserveSlotsUntil(seq);
                offset = SequenceDistance(_baseSeq, seq);
            }

            int idx = IndexForOffset((int)offset);
            ref Slot s = ref _slots[idx];

            if (!s.Occupied)
            {
                s.Set(seq, item, DateTime.UtcNow);
                _arrived++;
            }
            else
            {
                s.Set(seq, item, DateTime.UtcNow);
            }
        }

        private void AdvanceBaseBy(int n)
        {
            if (n <= 0) return;

            for (int i = 0; i < n && _countSlots > 0; i++)
            {
                int idx = IndexForOffset(0);
                _slots[idx].Clear();
                _baseSeq = MaskSeq(_baseSeq + 1u);
                _countSlots--;
            }
        }

        private void PullLockedLimited()
        {
            // ✅ 游戏串流优化：根据积压情况动态调整输出帧数，优先保证低延迟和稳定性
            int dynamicMax = _maxOutputPerPull;
            if (_countSlots > 200)
                dynamicMax = Math.Min(20, Math.Max(_maxOutputPerPull, _countSlots / 12));  // ✅ 积压非常严重时，最多20帧，快速处理
            else if (_countSlots > 100)
                dynamicMax = Math.Min(MAX_OUTPUT_PER_PULL, Math.Max(_maxOutputPerPull, _countSlots / 8));  // ✅ 积压严重时，最多25帧
            else if (_countSlots > 50)
                dynamicMax = Math.Min(MAX_OUTPUT_PER_PULL, _maxOutputPerPull * 4);  // ✅ 中等积压时，最多12帧
            else
                dynamicMax = Math.Min(MAX_OUTPUT_PER_PULL, _maxOutputPerPull * 3);  // ✅ 正常情况，最多9帧，保证低延迟和稳定性

            int outputs = 0;
            while (outputs < dynamicMax && PullLocked()) outputs++;
        }

        private bool PullLocked()
        {
            if (_countSlots == 0) return false;

            int idx = IndexForOffset(0);
            ref Slot s = ref _slots[idx];

            if (!s.Exists || !s.Occupied) return false;

            _output(s.Item!);
            s.Clear();
            _processed++;
            _arrived--;
            AdvanceBaseBy(1);
            return true;
        }

        private void CheckTimeoutLocked()
        {
            if (_countSlots == 0) return;

            var now = DateTime.UtcNow;
            int maxConsecutive = CalculateMaxConsecutive();
            int consecutive = 0;
            var remove = 0;
            bool hasTimeoutOccupied = false;  // ✅ 记录是否有已占用的slot超时

            for (int i = 0; i < _countSlots && consecutive < maxConsecutive; i++)
            {
                int idx = IndexForOffset(i);
                ref Slot s = ref _slots[idx];

                if (!s.Exists)
                {
                    remove++;
                    consecutive++;
                    continue;
                }

                bool isTimeout = false;
                if (!s.Occupied)
                {
                    if (s.ReservedTime != DateTime.MinValue)
                    {
                        var elapsed = (now - s.ReservedTime).TotalMilliseconds;
                        // ✅ 优化：对于未占用的slot，使用更长的超时时间，避免过早丢弃
                        if (elapsed > _timeoutMs * 3.0) isTimeout = true;
                    }
                    else
                    {
                        isTimeout = true;
                    }
                }
                else
                {
                    var elapsed = (now - s.ArrivalTime).TotalMilliseconds;
                    // ✅ 优化：对于已占用的slot，使用标准超时时间，但记录是否有超时
                    if (elapsed > _timeoutMs) 
                    {
                        isTimeout = true;
                        hasTimeoutOccupied = true;
                    }
                }

                if (isTimeout)
                {
                    if (s.Occupied)
                    {
                        // ✅ 优化：即使超时，也输出已占用的slot，避免帧丢失
                        _output(s.Item!);
                        _processed++;
                        _arrived--;
                    }
                    else
                    {
                        _timeoutDropped++;
                        _dropped++;
                    }
                    remove++;
                    consecutive++;
                    continue;
                }

                break;
            }

            if (remove == 0) return;

            AdvanceBaseBy(remove);
            
            // ✅ 优化：只有在有已占用的slot超时时才调用timeoutCallback，避免过于频繁的调用
            // 这可以减少不必要的关键帧请求，避免画面冻结
            if (hasTimeoutOccupied)
            {
                _timeoutCallback?.Invoke();
            }
            
            PullLockedLimited();
        }

        private int CalculateMaxConsecutive()
        {
            if (_arrived > 500) return 100;
            if (_arrived > 300) return 80;
            if (_arrived > 150) return 50;
            if (_arrived > 60) return 30;
            if (_arrived > 30) return 20;
            if (_arrived > 20) return 10;
            return 8;
        }

        #endregion

        #region Utilities & Slot

        private struct Slot
        {
            public bool Exists;
            public bool Occupied;
            public uint Seq;
            public T? Item;
            public DateTime ArrivalTime;
            public DateTime ReservedTime;

            public void Set(uint seq, T item, DateTime when)
            {
                Exists = true;
                Occupied = true;
                Seq = seq;
                Item = item;
                ArrivalTime = when;
                ReservedTime = DateTime.MinValue;
            }

            public void Reserve(DateTime when)
            {
                Exists = true;
                Occupied = false;
                ReservedTime = when;
                Item = null;
                ArrivalTime = DateTime.MinValue;
                Seq = 0;
            }

            public void Clear()
            {
                Exists = false;
                Occupied = false;
                Item = null;
                ArrivalTime = DateTime.MinValue;
                ReservedTime = DateTime.MinValue;
                Seq = 0;
            }
        }

        private static uint MaskSeq(uint v) => v & SEQ_MASK;

        private static uint SequenceDistance(uint from, uint to) => (to - from) & SEQ_MASK;

        private static bool IsNewer(uint seq, uint @base) => ((seq - @base) & SEQ_MASK) < HALF_SPACE;

        private int IndexForOffset(int offset)
        {
            int baseIndex = (int)(_baseSeq & (uint)_capacityMask);
            return (baseIndex + offset) & _capacityMask;
        }

        private static int NextPowerOfTwo(int v)
        {
            if (v < 1) return 1;
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            v++;
            return v;
        }

        private void ClearAllSlots()
        {
            for (int i = 0; i < _capacity; i++)
            {
                _slots[i].Clear();
            }
        }

        private void EnsureCapacity(int minRequired)
        {
            int required = Math.Max(minRequired, _sizeMin);
            if (_capacity < required)
            {
                int newCap = NextPowerOfTwo(Math.Clamp(required, _sizeMin, _sizeMax));
                if (newCap > _capacity)
                {
                    var newSlots = new Slot[newCap];
                    int newMask = newCap - 1;

                    for (int i = 0; i < _countSlots; i++)
                    {
                        int oldIdx = IndexForOffset(i);
                        int newIdx = (int)((_baseSeq + (uint)i) & (uint)newMask);
                        newSlots[newIdx] = _slots[oldIdx];
                    }

                    _slots = newSlots;
                    _capacity = newCap;
                    _capacityMask = newMask;
                }
            }
        }

        private void LogDropThrottled(uint seq, string reason)
        {
            _dropLogCount++;
            var now = DateTime.UtcNow;
            bool shouldLog = (now - _lastDropLog).TotalMilliseconds > DROP_LOG_THROTTLE_MS || _dropLogCount >= DROP_LOG_THROTTLE_COUNT;
            if (shouldLog)
            {
                _logger.LogWarning("ReorderQueue: dropping packet ({Reason}) seq={Seq}, base={Base}, slots={Slots}, cap={Cap}, dropcount={Count}",
                    reason, seq, _baseSeq, _countSlots, _capacity, _dropLogCount);
                _lastDropLog = now;
                _dropLogCount = 0;
            }
        }

        #endregion
    }
}