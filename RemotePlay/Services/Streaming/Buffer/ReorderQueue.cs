using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// Balanced-mode ReorderQueue for low-latency realtime video.
    /// - Small fixed-ish window (default 8)
    /// - Short adaptive timeout (clamped 4..12ms by default)
    /// - Drop-from-begin strategy to avoid large latency accumulation
    /// - No aggressive reset; only gentle rebase on extreme gap conditions
    /// </summary>
    public sealed class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFFu;
        private const uint HALF_SPACE = 0x8000u;

        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeq;
        private readonly Action<T> _output;
        private Action<T>? _drop;
        private readonly Func<T, bool>? _isKeyFrame;

        // slots circular buffer
        private Slot[] _slots;
        private int _capacity;
        private int _capacityMask;

        private uint _baseSeq;
        private int _countSlots;   // current window length (reserved slots)
        private int _arrived;      // number of occupied slots

        private readonly int _sizeMin;
        private readonly int _sizeMax;

        // Balanced defaults: small window
        private int _timeoutMsBase;      // base timeout in ms
        private int _timeoutMs;          // adaptive timeout used in checks
        private const int TIMEOUT_MIN = 4;
        private const int TIMEOUT_MAX = 500;  // 允许更大的超时（局域网可能需要50-200ms）

        private bool _initialized;

        // basic stats
        private ulong _processed;
        private ulong _dropped;
        private ulong _timeoutDropped;

        // adaptive jitter estimation (very lightweight)
        private readonly Queue<double> _iatWindow = new Queue<double>();
        private const int IAT_WINDOW = 32;
        private DateTime _lastArrival = DateTime.MinValue;
        private double _estimatedJitterMs = 0.0;

        // thresholds for behavior
        private readonly int _maxBufferFrames;   // recommended 8
        private readonly int _maxGap;            // recommended 12
        private readonly uint _maxResetGap;      // if needed > this, reject or rebase conservatively

        private readonly object _lock = new object();

        // output pacing limits: 每次只输出1帧，确保输出节奏绝对稳定（避免帧率锯齿）
        private readonly int _maxOutputPerPull = 1;

        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            Action<T>? dropCallback = null,
            Func<T, bool>? isKeyFrame = null,
            int maxBufferFrames = 8,      // Balanced default
            int maxGap = 12,
            int timeoutMsBase = 6)       // base timeout (ms)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _getSeq = getSeqNum ?? throw new ArgumentNullException(nameof(getSeqNum));
            _output = outputCallback ?? throw new ArgumentNullException(nameof(outputCallback));
            _drop = dropCallback;
            _isKeyFrame = isKeyFrame;

            _sizeMin = Math.Max(4, Math.Min(8, maxBufferFrames)); // ensure some sane floor
            // ✅ 移除32的上限限制，允许更大的窗口（局域网可能需要192或更大）
            _maxBufferFrames = Math.Max(4, maxBufferFrames);  // 只保留最小值限制
            _sizeMax = Math.Max(_sizeMin, Math.Max(64, _maxBufferFrames * 2)); // 增加容量上限以支持大窗口

            int start = Math.Clamp(_maxBufferFrames, _sizeMin, _sizeMax);
            _capacity = NextPowerOfTwo(start);
            _capacityMask = _capacity - 1;
            _slots = new Slot[_capacity];

            _timeoutMsBase = Math.Clamp(timeoutMsBase, TIMEOUT_MIN, TIMEOUT_MAX);
            _timeoutMs = _timeoutMsBase;

            _maxGap = Math.Clamp(maxGap, 4, Math.Max(32, maxBufferFrames)); // 允许更大的gap容忍
            _maxResetGap = (uint)(_maxBufferFrames * 8); // 增加极端gap阈值，减少误判

            _initialized = false;
        }

        #region Public API

        public void Push(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            uint seq = MaskSeq(_getSeq(item));
            DateTime now = DateTime.UtcNow;

            lock (_lock)
            {
                if (!_initialized)
                {
                    Initialize(seq, item, now);
                    return;
                }

                // update inter-arrival / jitter
                UpdateIatAndJitter(now);

                // quick distance checks
                uint dist = SequenceDistance(_baseSeq, seq);
                if (dist < (uint)_countSlots)
                {
                    // falls into current window
                    PutInSlot(seq, item, now);
                    if (seq == _baseSeq)
                        PullLockedSingle(); // pull aggressively but 1-2 frames
                    return;
                }

                // older than base?
                if (!IsNewer(seq, _baseSeq))
                {
                    uint gap = SequenceDistance(_baseSeq, seq);
                    // likely wrap-around?
                    bool likelyWrap = _baseSeq > HALF_SPACE && seq < HALF_SPACE && gap > HALF_SPACE;
                    if (likelyWrap)
                    {
                        GentleRebase(seq, item, "wrap-around", now);
                        return;
                    }

                    // 如果是小gap（可能是乱序），尝试放入窗口而不是直接丢弃
                    if (gap < (uint)_maxGap * 2)
                    {
                        // 尝试放入窗口（可能是乱序到达的包）
                        if (gap < (uint)_countSlots)
                        {
                            PutInSlot(seq, item, now);
                            return;
                        }
                        // gap较大但仍在容忍范围内，尝试rebase
                        GentleRebase(seq, item, "out-of-order-old", now);
                        return;
                    }

                    // gap太大，确实是旧包：drop
                    _drop?.Invoke(item);
                    _dropped++;
                    return;
                }

                // seq is newer, needs room
                uint endSeq = MaskSeq(_baseSeq + (uint)_countSlots);
                uint needed = SequenceDistance(endSeq, MaskSeq(seq + 1u));

                // if needed gap is absurdly large, reject instead of expanding/resetting
                if (needed > _maxResetGap)
                {
                    // extreme gap: reject and log
                    _logger.LogWarning("ReorderQueue: extreme needed gap {Gap} > maxResetGap, rejecting seq {Seq}", needed, seq);
                    _drop?.Invoke(item);
                    _dropped++;
                    return;
                }

                // limit growth: if needed would push beyond configured max buffer, drop oldest slots (Begin) to make room
                if (_countSlots + needed > _maxBufferFrames)
                {
                    // free up minimal slots from begin (drop oldest) to make room
                    uint shrink = (uint)(_countSlots + needed - _maxBufferFrames);
                    FreeFromBegin(shrink);
                }

                // ensure internal capacity (simple expand but bounded)
                if (!EnsureSpaceFor(needed))
                {
                    // cannot make room -> drop packet
                    _drop?.Invoke(item);
                    _dropped++;
                    return;
                }

                ReserveSlotsUntil(seq, now);
                PutInSlot(seq, item, now);
                if (seq == _baseSeq)
                    PullLockedSingle();
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
                // soft timeout check to advance base if necessary
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

        public void SetDropCallback(Action<T>? cb)
        {
            lock (_lock) { _drop = cb; }
        }

        #endregion

        #region Core logic (balanced, minimal scanning)

        private void Initialize(uint seq, T item, DateTime now)
        {
            _baseSeq = seq;
            ClearAllSlots();
            EnsureCapacity(1);
            int idx = IndexForOffset(0);
            _slots[idx].Set(seq, item, now);
            _countSlots = 1;
            _arrived = 1;
            _initialized = true;
            PullLockedSingle();
        }

        private void GentleRebase(uint seq, T item, string reason, DateTime now)
        {
            // Only used for wrap-around or when base window is dead.
            // Move base forward in a conservative manner; do not wipe metrics.
            _logger.LogWarning("ReorderQueue: gentle rebase ({Reason}) {Begin} -> {Seq}", reason, _baseSeq, seq);

            uint gap = SequenceDistance(_baseSeq, seq);
            if (gap == 0)
            {
                // same as base
                int idx0 = IndexForOffset(0);
                ref Slot s0 = ref _slots[idx0];
                bool wasOcc = s0.Occupied;
                s0.Set(seq, item, now);
                if (!wasOcc) _arrived++;
                if (_countSlots == 0) _countSlots = 1;
                PullLockedSingle();
                return;
            }

            // if gap moderately large but < maxResetGap, advance base by gap but treat skipped frames as dropped
            int toAdvance = (int)Math.Min((uint)_countSlots, gap);
            for (int i = 0; i < toAdvance; i++)
            {
                int idx = IndexForOffset(0);
                ref Slot s = ref _slots[idx];
                if (s.Exists && s.Occupied)
                {
                    // drop them (old frames are meaningless)
                    _drop?.Invoke(s.Item!);
                    _dropped++;
                    _arrived--;
                    _processed++;
                }
                s.Clear();
                _baseSeq = MaskSeq(_baseSeq + 1u);
                _countSlots--;
            }

            if (gap > (uint)toAdvance)
            {
                uint remaining = gap - (uint)toAdvance;
                _baseSeq = MaskSeq(_baseSeq + remaining);
            }

            EnsureCapacity(1);
            int newIdx = IndexForOffset(0);
            _slots[newIdx].Set(seq, item, now);
            _countSlots = Math.Max(1, _countSlots);
            _arrived++;
            PullLockedSingle();
        }

        // Reserve empty slots up to seq
        private void ReserveSlotsUntil(uint seq, DateTime now)
        {
            uint endSeq = MaskSeq(_baseSeq + (uint)_countSlots);
            uint needed = SequenceDistance(endSeq, MaskSeq(seq + 1u));
            for (uint i = 0; i < needed; i++)
            {
                int idx = IndexForOffset(_countSlots);
                if (!_slots[idx].Exists)
                {
                    _slots[idx].Reserve(now);
                }
                _countSlots++;
            }
        }

        private bool EnsureSpaceFor(uint needed)
        {
            if (_countSlots + needed > _sizeMax) return false;
            int free = _capacity - _countSlots;
            if (needed <= (uint)free) return true;
            // try expand but limited to _sizeMax
            if (TryExpandToFit((int)needed)) return true;
            return false;
        }

        private bool TryExpandToFit(int needed)
        {
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
                return true;
            }
            return false;
        }

        private void PutInSlot(uint seq, T item, DateTime now)
        {
            uint offset = SequenceDistance(_baseSeq, seq);
            if (offset >= (uint)_countSlots)
            {
                ReserveSlotsUntil(seq, now);
                offset = SequenceDistance(_baseSeq, seq);
            }
            int idx = IndexForOffset((int)offset);
            ref Slot s = ref _slots[idx];
            if (!s.Occupied)
            {
                s.Set(seq, item, now);
                _arrived++;
            }
            else
            {
                // overwrite duplicate / retransmit
                s.Set(seq, item, now);
            }

            // ✅ 移除这个自动丢弃逻辑 - 它会导致大窗口场景下频繁丢包
            // 缓冲区大小应该由其他机制（如超时、主动flush）控制，而不是在PutInSlot时强制丢弃
            // 保留注释作为历史参考：
            // if (_arrived > _maxBufferFrames * 1.5f)  // 只在超出50%时才考虑丢弃
            // {
            //     // 可选：只在极端情况下丢弃，而不是每次超过阈值就丢弃
            // }
        }

        // Free 'n' slots from begin by dropping/consuming them
        private void FreeFromBegin(uint n)
        {
            uint freed = 0;
            while (freed < n && _countSlots > 0)
            {
                int idx = IndexForOffset(0);
                ref Slot s = ref _slots[idx];
                if (s.Exists && s.Occupied)
                {
                    // drop oldest
                    _drop?.Invoke(s.Item!);
                    _dropped++;
                    _arrived--;
                }
                s.Clear();
                _baseSeq = MaskSeq(_baseSeq + 1u);
                _countSlots--;
                freed++;
            }
        }

        // Single-frame pulling (avoid bursts)
        private void PullLockedSingle()
        {
            int outputs = 0;
            while (outputs < _maxOutputPerPull && PullLocked()) outputs++;
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
            _baseSeq = MaskSeq(_baseSeq + 1u);
            _countSlots--;
            return true;
        }

        // Timeout scanning: only scan a few head slots and advance when expired
        private void CheckTimeoutLocked()
        {
            if (_countSlots == 0) return;
            var now = DateTime.UtcNow;
            int scanned = 0;
            int removed = 0;
            int scanLimit = Math.Min(4, _countSlots); // very small scan
            for (int i = 0; i < scanLimit; i++)
            {
                int idx = IndexForOffset(i);
                ref Slot s = ref _slots[idx];

                if (!s.Exists)
                {
                    scanned++;
                    removed++;
                    continue;
                }

                bool timedOut = false;
                if (!s.Occupied)
                {
                    // reserved but never filled
                    var elapsed = (now - s.ReservedTime).TotalMilliseconds;
                    if (elapsed > _timeoutMs) timedOut = true;
                }
                else
                {
                    var elapsed = (now - s.ArrivalTime).TotalMilliseconds;
                    if (elapsed > _timeoutMs) timedOut = true;
                }

                if (timedOut)
                {
                    if (s.Occupied)
                    {
                        // output late frame rather than drop? Balanced mode: output to keep continuity
                        _output(s.Item!);
                        _processed++;
                        _arrived--;
                    }
                    else
                    {
                        _timeoutDropped++;
                        _dropped++;
                    }
                    removed++;
                    scanned++;
                    continue;
                }

                // head not timed out -> stop
                break;
            }
            if (removed > 0)
            {
                // advance by removed
                AdvanceBaseBy(removed);
                // after advancing, try pull few frames
                PullLockedSingle();
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

        #endregion

        #region Utilities & slot

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
                Seq = 0;
                Item = null;
                ReservedTime = when;
                ArrivalTime = DateTime.MinValue;
            }

            public void Clear()
            {
                Exists = false;
                Occupied = false;
                Seq = 0;
                Item = null;
                ArrivalTime = DateTime.MinValue;
                ReservedTime = DateTime.MinValue;
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
            for (int i = 0; i < _capacity; i++) _slots[i].Clear();
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

        #endregion

        #region Jitter / adaptive timeout (lightweight)

        private void UpdateIatAndJitter(DateTime now)
        {
            if (_lastArrival != DateTime.MinValue)
            {
                double iat = (now - _lastArrival).TotalMilliseconds;
                _iatWindow.Enqueue(iat);
                if (_iatWindow.Count > IAT_WINDOW) _iatWindow.Dequeue();

                if (_iatWindow.Count >= 4)
                {
                    double mean = _iatWindow.Average();
                    double var = _iatWindow.Sum(x => (x - mean) * (x - mean)) / _iatWindow.Count;
                    _estimatedJitterMs = Math.Sqrt(var);
                }
                // adaptive timeout: base + 1.5*jitter, clamped small
                double adapt = _timeoutMsBase + 1.5 * _estimatedJitterMs;
                _timeoutMs = (int)Math.Clamp(adapt, TIMEOUT_MIN, TIMEOUT_MAX);
            }
            _lastArrival = now;
        }

        #endregion
    }
}
