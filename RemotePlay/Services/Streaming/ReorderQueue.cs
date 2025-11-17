using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming
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

            if (_buffer.TryGetValue(_begin, out var entry))
            {
                if (!entry.IsSet)
                {
                    _logger.LogWarning("Timeout: reserved slot seq={Seq} never received, skipping", _begin);
                    toRemove.Add(_begin);
                    _begin = MaskSeq(_begin + 1);
                    _count--;
                    _totalDropped++;
                    _totalTimeoutDropped++;
                    _timeoutCallback?.Invoke();
                }
                else
                {
                    var elapsed = (now - entry.ArrivalTime).TotalMilliseconds;
                    if (elapsed > _timeoutMs)
                    {
                        _outputCallback(entry.Item!);
                        toRemove.Add(_begin);
                        _begin = MaskSeq(_begin + 1);
                        _count--;
                        _totalProcessed++;

                        uint skipped = SequenceDistance(_begin - 1, _begin);
                        if (skipped > 1)
                        {
                            _totalDropped += skipped - 1;
                            _totalTimeoutDropped += skipped - 1;
                            _logger.LogWarning("Timeout: output seq={Seq}, skipped={Skipped}, elapsed={Elapsed}ms",
                                _begin - 1, skipped - 1, elapsed);
                        }

                        _timeoutCallback?.Invoke();
                    }
                }
            }

            foreach (var seq in toRemove)
            {
                _buffer.Remove(seq);
            }

            if (toRemove.Count > 0)
            {
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
