using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Buffer
{
    public enum ReorderQueueDropStrategy
    {
        Begin,
        End
    }

    public class ReorderQueue<T> where T : class
    {
        private const uint SEQ_MASK = 0xFFFF;
        private const int DEFAULT_SIZE_START = 32;
        private const int DEFAULT_SIZE_MIN = 8;
        private const int DEFAULT_SIZE_MAX = 128;
        private const int DEFAULT_TIMEOUT_MS = 50;

        private readonly ILogger _logger;
        private readonly Func<T, uint> _getSeqNum;
        private readonly Action<T> _outputCallback;
        private Action<T>? _dropCallback;
        private Action? _timeoutCallback;

        private readonly SortedDictionary<uint, QueueEntry> _buffer;
        private readonly object _lock = new object();

        private uint _begin;
        private int _count;
        private int _currentSize;
        private readonly int _sizeMin;
        private readonly int _sizeMax;
        private readonly int _timeoutMs;
        private ReorderQueueDropStrategy _dropStrategy;

        private bool _initialized = false;

        private ulong _totalProcessed = 0;
        private ulong _totalDropped = 0;
        private ulong _totalTimeoutDropped = 0;

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
        }

        public void Push(T item)
        {
            lock (_lock)
            {
                uint seqNum = _getSeqNum(item) & SEQ_MASK;

                if (!_initialized)
                {
                    _begin = seqNum;
                    _count = 0;
                    _initialized = true;
                    _outputCallback(item);
                    _begin = MaskSeq(_begin + 1);
                    _totalProcessed++;
                    return;
                }

                uint end = MaskSeq(_begin + (uint)_count);

                if (Ge(seqNum, _begin) && Lt(seqNum, end))
                {
                    if (_buffer.TryGetValue(seqNum, out var entry))
                    {
                        if (entry.IsSet)
                            goto drop_it;
                        entry.Item = item;
                        entry.ArrivalTime = DateTime.UtcNow;
                    }
                    else
                    {
                        _buffer[seqNum] = new QueueEntry
                        {
                            Item = item,
                            ArrivalTime = DateTime.UtcNow
                        };
                    }

                    if (seqNum == _begin)
                    {
                        Pull();
                    }
                    return;
                }

                if (Lt(seqNum, _begin))
                    goto drop_it;

                uint freeElems = (uint)(_currentSize - _count);
                uint totalEnd = MaskSeq(end + freeElems);
                uint newEnd = MaskSeq(seqNum + 1);

                if (Lt(totalEnd, newEnd))
                {
                    if (_dropStrategy == ReorderQueueDropStrategy.End)
                        goto drop_it;

                    while (_count > 0 && Lt(totalEnd, newEnd))
                    {
                        if (_buffer.TryGetValue(_begin, out var entry) && entry.IsSet)
                            _dropCallback?.Invoke(entry.Item!);
                        _buffer.Remove(_begin);
                        _begin = MaskSeq(_begin + 1);
                        _count--;
                        freeElems = (uint)(_currentSize - _count);
                        totalEnd = MaskSeq(end + freeElems);
                    }

                    if (_count == 0)
                        _begin = seqNum;
                }

                end = MaskSeq(_begin + (uint)_count);
                while (Lt(end, newEnd))
                {
                    if (!_buffer.ContainsKey(end))
                    {
                        _buffer[end] = new QueueEntry
                        {
                            Item = null,
                            ArrivalTime = DateTime.MinValue
                        };
                    }
                    _count++;
                    end = MaskSeq(_begin + (uint)_count);
                }

                if (_buffer.TryGetValue(seqNum, out var newEntry))
                {
                    newEntry.Item = item;
                    newEntry.ArrivalTime = DateTime.UtcNow;
                }
                else
                {
                    _buffer[seqNum] = new QueueEntry
                    {
                        Item = item,
                        ArrivalTime = DateTime.UtcNow
                    };
                }

                if (seqNum == _begin)
                {
                    Pull();
                }
                return;

            drop_it:
                _dropCallback?.Invoke(item);
                _totalDropped++;
            }
        }

        public void Flush(bool force = false)
        {
            lock (_lock)
            {
                if (force)
                {
                    while (Pull()) { }
                    return;
                }
                CheckTimeout();
            }
        }

        public (ulong processed, ulong dropped, ulong timeoutDropped, int bufferSize) GetStats()
        {
            lock (_lock)
            {
                int arrivedCount = _buffer.Values.Count(e => e.IsSet);
                return (_totalProcessed, _totalDropped, _totalTimeoutDropped, arrivedCount);
            }
        }

        public void SetDropStrategy(ReorderQueueDropStrategy strategy)
        {
            lock (_lock)
            {
                _dropStrategy = strategy;
            }
        }

        public void SetDropCallback(Action<T>? callback)
        {
            lock (_lock)
            {
                _dropCallback = callback;
            }
        }

        public void SetTimeoutCallback(Action? callback)
        {
            if (callback == null)
                return;

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

        public void Reset()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _initialized = false;
                _begin = 0;
                _count = 0;
            }
        }

        private bool Pull()
        {
            if (_count == 0)
                return false;

            if (!_buffer.TryGetValue(_begin, out var entry))
                return false;

            if (!entry.IsSet)
                return false;

            _outputCallback(entry.Item!);
            _buffer.Remove(_begin);
            _begin = MaskSeq(_begin + 1);
            _count--;
            _totalProcessed++;

            while (_count > 0)
            {
                if (!_buffer.TryGetValue(_begin, out var nextEntry))
                    break;
                if (!nextEntry.IsSet)
                    break;
                _outputCallback(nextEntry.Item!);
                _buffer.Remove(_begin);
                _begin = MaskSeq(_begin + 1);
                _count--;
                _totalProcessed++;
            }

            return true;
        }

        private void CheckTimeout()
        {
            if (_count == 0)
                return;

            var now = DateTime.UtcNow;
            var toRemove = new List<uint>();
            uint currentSeq = _begin;
            int consecutiveTimeouts = 0;
            const int MAX_CONSECUTIVE_TIMEOUTS = 20; // 最多连续跳过 20 个超时的 slot

            // ✅ 关键修复：检查所有超时的 slot，而不仅仅是头部
            // 当网络变差时，可能会有多个连续的 slot 超时，需要批量清理
            for (int i = 0; i < _count && consecutiveTimeouts < MAX_CONSECUTIVE_TIMEOUTS; i++)
            {
                if (!_buffer.TryGetValue(currentSeq, out var entry))
                {
                    // Slot 不存在，跳过
                    currentSeq = MaskSeq(currentSeq + 1);
                    continue;
                }

                bool isTimeout = false;
                if (!entry.IsSet)
                {
                    // 未收到的 slot（ArrivalTime == DateTime.MinValue）
                    // ✅ 对于未收到的 slot，直接认为超时，避免长时间等待
                    // 这样可以更快地跳过丢失的包，减少延迟累积
                    isTimeout = true;
                }
                else
                {
                    // 已收到的 slot，检查是否超时
                    var elapsed = (now - entry.ArrivalTime).TotalMilliseconds;
                    if (elapsed > _timeoutMs)
                    {
                        isTimeout = true;
                    }
                }

                if (isTimeout)
                {
                    if (entry.IsSet)
                    {
                        // 已收到但超时，输出它
                        _outputCallback(entry.Item!);
                        _totalProcessed++;
                    }
                    else
                    {
                        // 未收到，记录丢失
                        _totalDropped++;
                        _totalTimeoutDropped++;
                    }

                    toRemove.Add(currentSeq);
                    consecutiveTimeouts++;
                    currentSeq = MaskSeq(currentSeq + 1);
                }
                else
                {
                    // 遇到未超时的 slot，停止批量清理
                    break;
                }
            }

            // 批量移除超时的 slot
            if (toRemove.Count > 0)
            {
                // ✅ 在移除前记录日志（因为移除后无法再访问 entry）
                bool hasUnreceived = false;
                foreach (var seq in toRemove)
                {
                    if (_buffer.TryGetValue(seq, out var entry) && !entry.IsSet)
                    {
                        hasUnreceived = true;
                        break;
                    }
                }
                
                // 记录批量超时（减少日志频率）
                if (toRemove.Count > 5)
                {
                    _logger.LogWarning("Timeout: batch skipped {Count} slots (seq {From} to {To})",
                        toRemove.Count, toRemove[0], toRemove[toRemove.Count - 1]);
                }
                else if (hasUnreceived)
                {
                    // 只记录第一个未收到的 slot，避免日志过多
                    _logger.LogWarning("Timeout: reserved slot seq={Seq} never received, skipping", toRemove[0]);
                }

                // 移除超时的 slot
                foreach (var seq in toRemove)
                {
                    _buffer.Remove(seq);
                }

                // 更新 _begin 和 _count
                _begin = currentSeq;
                _count -= toRemove.Count;

                // 触发超时回调（只触发一次，避免重复）
                _timeoutCallback?.Invoke();

                Pull();
            }
        }

        private bool Lt(uint a, uint b)
        {
            if (a == b)
                return false;
            int diff = (int)(b & SEQ_MASK) - (int)(a & SEQ_MASK);
            return (a < b && diff < 0x8000) || (a > b && -diff > 0x8000);
        }

        private bool Gt(uint a, uint b)
        {
            if (a == b)
                return false;
            int diff = (int)(b & SEQ_MASK) - (int)(a & SEQ_MASK);
            return (a < b && diff > 0x8000) || (a > b && -diff < 0x8000);
        }

        private bool Ge(uint a, uint b)
        {
            return a == b || Gt(a, b);
        }

        private uint SequenceDistance(uint from, uint to)
        {
            return (to - from) & SEQ_MASK;
        }

        private static uint MaskSeq(uint value) => value & SEQ_MASK;

        private class QueueEntry
        {
            public T? Item { get; set; }
            public DateTime ArrivalTime { get; set; }
            public bool IsSet => Item != null;
        }
    }
}

