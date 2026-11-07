using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// 16 位序号的包重排队列。
    /// 不变量：缓冲中的包都位于 [_baseSeq, _baseSeq + _window) 内；_baseSeq 只前进、且不会超过最新收到的序号 + 1；
    /// _buffered 恒等于已占用的槽位数。序号回绕由模运算自然处理。
    /// 输出在锁内按序执行，保证多线程调用 Push/Flush 时顺序不乱。
    /// </summary>
    public sealed class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFFu;
        private const uint HALF_SPACE = 0x8000u;
        private const int TIMEOUT_MIN_MS = 2;
        private const int TIMEOUT_MAX_MS = 500;

        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeq;
        private readonly Action<T> _output;
        private Action<T>? _drop;

        private readonly T?[] _slots;
        private readonly int _capacityMask;
        private readonly int _window;
        private readonly uint _resetGap;
        private readonly long _timeoutTicks;

        private bool _initialized;
        private uint _baseSeq;
        private int _buffered;
        private long _holeSinceTicks;
        private uint _holeBaseSeq;

        private ulong _processed;
        private ulong _dropped;
        private ulong _timeoutDropped;

        private readonly object _lock = new object();

        /// <param name="maxBufferFrames">重排窗口（单位：包）</param>
        /// <param name="maxGap">保留参数，未使用</param>
        /// <param name="timeoutMsBase">队首缺包时最多等待的时间，超时后跳过缺口</param>
        public ReorderQueue(
            ILogger logger,
            Func<T, uint> getSeqNum,
            Action<T> outputCallback,
            Action<T>? dropCallback = null,
            Func<T, bool>? isKeyFrame = null,
            int maxBufferFrames = 8,
            int maxGap = 12,
            int timeoutMsBase = 14)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _getSeq = getSeqNum ?? throw new ArgumentNullException(nameof(getSeqNum));
            _output = outputCallback ?? throw new ArgumentNullException(nameof(outputCallback));
            _drop = dropCallback;

            _window = Math.Clamp(maxBufferFrames, 4, 4096);
            int capacity = NextPowerOfTwo(_window);
            _slots = new T?[capacity];
            _capacityMask = capacity - 1;
            _resetGap = (uint)Math.Max(_window * 8, 1024);

            int timeoutMs = Math.Clamp(timeoutMsBase, TIMEOUT_MIN_MS, TIMEOUT_MAX_MS);
            _timeoutTicks = (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        }

        #region Public API

        public void Push(T item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            uint seq = _getSeq(item) & SEQ_MASK;

            lock (_lock)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    _baseSeq = seq;
                }

                uint ahead = Distance(_baseSeq, seq);
                if (ahead >= HALF_SPACE)
                {
                    uint behind = Distance(seq, _baseSeq);
                    if (behind <= _resetGap)
                    {
                        // 迟到包：它所在的位置已经被跳过或输出
                        DropItem(item);
                        return;
                    }

                    _logger.LogWarning("ReorderQueue: 序号跳变（落后 {Behind}），重置到 {Seq}", behind, seq);
                    DrainAll();
                    _baseSeq = seq;
                    ahead = 0;
                }
                else if (ahead >= _resetGap)
                {
                    _logger.LogWarning("ReorderQueue: 序号跳变（领先 {Ahead}），重置到 {Seq}", ahead, seq);
                    DrainAll();
                    _baseSeq = seq;
                    ahead = 0;
                }

                if (ahead >= (uint)_window)
                {
                    // 超出窗口：放弃等待最老的缺口，把队首推进到能容纳该包的位置
                    AdvanceBaseTo((seq - (uint)_window + 1u) & SEQ_MASK);
                }

                int idx = (int)(seq & (uint)_capacityMask);
                if (_slots[idx] != null)
                {
                    DropItem(item);
                    return;
                }

                _slots[idx] = item;
                _buffered++;

                PullContiguous();
                UpdateHoleTimer();
            }
        }

        public void Flush(bool force = false)
        {
            lock (_lock)
            {
                if (_buffered == 0)
                {
                    return;
                }

                if (force)
                {
                    DrainAll();
                    return;
                }

                if (_holeSinceTicks != 0 && Stopwatch.GetTimestamp() - _holeSinceTicks >= _timeoutTicks)
                {
                    SkipHolesAtBase();
                    PullContiguous();
                    UpdateHoleTimer();
                }
            }
        }

        public (ulong processed, ulong dropped, ulong timeoutDropped, int bufferSize) GetStats()
        {
            lock (_lock)
            {
                return (_processed, _dropped, _timeoutDropped, _buffered);
            }
        }

        public void SetDropCallback(Action<T>? cb)
        {
            lock (_lock) { _drop = cb; }
        }

        #endregion

        #region Core logic

        private void PullContiguous()
        {
            while (_buffered > 0)
            {
                int idx = (int)(_baseSeq & (uint)_capacityMask);
                var item = _slots[idx];
                if (item == null)
                {
                    return;
                }

                _slots[idx] = null;
                _buffered--;
                _baseSeq = (_baseSeq + 1u) & SEQ_MASK;
                Emit(item);
            }
        }

        /// <summary>
        /// 跳过队首连续的缺口，直到遇到下一个已到达的包
        /// </summary>
        private void SkipHolesAtBase()
        {
            int guard = _slots.Length;
            while (_buffered > 0 && guard-- > 0 && _slots[_baseSeq & (uint)_capacityMask] == null)
            {
                _timeoutDropped++;
                _baseSeq = (_baseSeq + 1u) & SEQ_MASK;
            }
        }

        /// <summary>
        /// 把队首推进到 target：途经的已到达包按序输出，缺口直接跳过
        /// </summary>
        private void AdvanceBaseTo(uint target)
        {
            while (_baseSeq != target && _buffered > 0)
            {
                int idx = (int)(_baseSeq & (uint)_capacityMask);
                var item = _slots[idx];
                if (item != null)
                {
                    _slots[idx] = null;
                    _buffered--;
                    Emit(item);
                }
                else
                {
                    _timeoutDropped++;
                }
                _baseSeq = (_baseSeq + 1u) & SEQ_MASK;
            }
            _baseSeq = target;
        }

        /// <summary>
        /// 按序输出全部缓冲的包（跳过缺口）
        /// </summary>
        private void DrainAll()
        {
            int guard = _slots.Length;
            while (_buffered > 0 && guard-- > 0)
            {
                int idx = (int)(_baseSeq & (uint)_capacityMask);
                var item = _slots[idx];
                if (item != null)
                {
                    _slots[idx] = null;
                    _buffered--;
                    Emit(item);
                }
                _baseSeq = (_baseSeq + 1u) & SEQ_MASK;
            }
            _holeSinceTicks = 0;
        }

        private void UpdateHoleTimer()
        {
            if (_buffered == 0)
            {
                _holeSinceTicks = 0;
            }
            else if (_holeSinceTicks == 0 || _holeBaseSeq != _baseSeq)
            {
                _holeSinceTicks = Stopwatch.GetTimestamp();
                _holeBaseSeq = _baseSeq;
            }
        }

        private void Emit(T item)
        {
            _processed++;
            _output(item);
        }

        private void DropItem(T item)
        {
            _dropped++;
            _drop?.Invoke(item);
        }

        #endregion

        #region Utilities

        private static uint Distance(uint from, uint to) => (to - from) & SEQ_MASK;

        private static int NextPowerOfTwo(int v)
        {
            if (v < 1) return 1;
            v--;
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return v + 1;
        }

        #endregion
    }
}
