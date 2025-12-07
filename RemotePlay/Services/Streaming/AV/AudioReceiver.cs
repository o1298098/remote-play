using RemotePlay.Services.Streaming.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 高稳定性音频接收器：无爆音、无周期性静音、带丢帧回调、正确处理 wrap-around
    /// </summary>
    public class AudioReceiver
    {
        private readonly ILogger<AudioReceiver>? _logger;

        private ushort _nextExpected = 0;
        private bool _initialized = false;
        private ushort _lastJumpTarget = 0;
        private ushort _lastJumpSource = 0;
        private int _jumpBackCount = 0;
        private DateTime _lastCleanTime = DateTime.MinValue;

        private Action<int>? _onFrameLossCallback;

        // Jitter Buffer：保证排序、解决乱序、防止爆音
        private readonly Dictionary<ushort, byte[]> _buffer = new();
        private readonly object _lock = new();

        private const int JITTER_MIN = 6;
        private const int JITTER_MAX = 256;
        private const int MAX_SKIP = 20;
        private const int EXTREME_JUMP_THRESHOLD = 50;

        public AudioReceiver(ILogger<AudioReceiver>? logger = null)
        {
            _logger = logger;
        }

        public void SetFrameLossCallback(Action<int>? cb)
        {
            _onFrameLossCallback = cb;
        }

        public void SetHeader(byte[] header) { }

        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[]> onFrameReady)
        {
            if (packet.Type != HeaderType.AUDIO)
                return;

            int unitSize = packet.AudioUnitSize;
            int srcCount = packet.UnitsSrc;
            int total = packet.UnitsTotal;

            if (unitSize * total != decryptedData.Length)
            {
                _logger?.LogError("Audio size mismatch");
                return;
            }

            for (int i = 0; i < srcCount; i++)
            {
                ushort index = (ushort)((packet.FrameIndex + i) & 0xFFFF);

                if (!_initialized)
                {
                    _initialized = true;
                    _nextExpected = index;
                }

                // 复制该 unit
                byte[] data = new byte[unitSize];
                System.Buffer.BlockCopy(decryptedData, i * unitSize, data, 0, unitSize);

                Add(index, data);
            }

            Flush(onFrameReady);
        }

        private void Add(ushort index, byte[] data)
        {
            lock (_lock)
            {
                if (_buffer.ContainsKey(index))
                    return;

                // 过滤距离_nextExpected过远的旧数据，防止buffer中同时存在新旧两批数据
                if (_initialized)
                {
                    int distance = SeqGap(_nextExpected, index);
                    if (distance > 1000)
                    {
                        return;
                    }
                }

                if (_buffer.Count >= JITTER_MAX)
                {
                    // 清除最旧的 key（距离 _nextExpected 最远）
                    ushort victim = _buffer.Keys
                        .OrderBy(k => SeqDistance(k, _nextExpected))
                        .Last();

                    _buffer.Remove(victim);
                }

                _buffer[index] = data;
            }
        }

        private void Flush(Action<byte[]> output)
        {
            lock (_lock)
            {
                if (_buffer.Count < JITTER_MIN)
                    return;

                const int MAX_OUTPUT_PER_FLUSH = 10;
                int outputCount = 0;
                ushort previousExpected = _nextExpected;

                while (_buffer.TryGetValue(_nextExpected, out var data) && outputCount < MAX_OUTPUT_PER_FLUSH)
                {
                    _buffer.Remove(_nextExpected);

                    output(data);

                    _nextExpected = (ushort)((_nextExpected + 1) & 0xFFFF);
                    outputCount++;
                }

                if (_buffer.Count == 0)
                    return;

                var first = _buffer.Keys
                    .OrderBy(k => SeqDistance(k, _nextExpected))
                    .First();

                int gap = SeqGap(_nextExpected, first);

                // wrap-around 处理
                bool wrap =
                    (gap > 30000) ||
                    (_nextExpected > 60000 && first < 500) ||
                    (_nextExpected < 500 && first > 60000) ||
                    (_nextExpected < 1000 && first > 60000 && gap > 8000) ||
                    (_nextExpected > 60000 && first < 1000 && gap > 8000);

                if (wrap)
                {
                    _onFrameLossCallback?.Invoke(gap);
                    _lastJumpSource = _nextExpected;
                    _nextExpected = first;
                    _lastJumpTarget = first;
                    _jumpBackCount = 0;
                    return;
                }

                // 跳帧逻辑
                if (gap > 0 && gap <= MAX_SKIP)
                {
                    _onFrameLossCallback?.Invoke(gap);
                    _lastJumpSource = _nextExpected;
                    _nextExpected = first;
                    _lastJumpTarget = first;
                    _jumpBackCount = 0;
                }
                else if (gap > MAX_SKIP && gap < 30000)
                {
                    // 极端跳帧防护：分批处理，避免一次性跳太多导致解码器爆音
                    if (gap > EXTREME_JUMP_THRESHOLD)
                    {
                        // 检测来回跳转
                        if (_lastJumpTarget != 0 && _lastJumpSource != 0)
                        {
                            int sourceToLastTarget = SeqGap(_nextExpected, _lastJumpTarget);
                            int firstToLastSource = SeqGap(first, _lastJumpSource);
                            
                            bool isJumpBack = (sourceToLastTarget < 100 || firstToLastSource < 100) && 
                                (gap > 2000 || sourceToLastTarget > 2000);
                            
                            if (isJumpBack)
                            {
                                _jumpBackCount++;
                                
                                if (_jumpBackCount >= 1)
                                {
                                    var timeSinceLastClean = DateTime.UtcNow - _lastCleanTime;
                                    bool frequentClean = timeSinceLastClean.TotalMilliseconds < 100;
                                    int KEEP_RANGE = frequentClean ? 50 : 200;
                                    
                                    var keysToRemove = _buffer.Keys
                                        .Where(k => SeqGap(_nextExpected, k) > KEEP_RANGE)
                                        .ToList();
                                    
                                    foreach (var key in keysToRemove)
                                    {
                                        _buffer.Remove(key);
                                    }
                                    
                                    _lastCleanTime = DateTime.UtcNow;
                                    _jumpBackCount = 0;
                                    _lastJumpTarget = 0;
                                    _lastJumpSource = 0;
                                    
                                    if (_buffer.Count > 0)
                                    {
                                        first = _buffer.Keys
                                            .OrderBy(k => SeqDistance(k, _nextExpected))
                                            .First();
                                        gap = SeqGap(_nextExpected, first);
                                        
                                        if (gap > KEEP_RANGE)
                                        {
                                            _onFrameLossCallback?.Invoke(gap);
                                            _nextExpected = first;
                                            _lastJumpSource = previousExpected;
                                            _lastJumpTarget = first;
                                            return;
                                        }
                                    }
                                    else
                                    {
                                        return;
                                    }
                                }
                            }
                            else
                            {
                                _jumpBackCount = 0;
                            }
                        }
                        
                        // 检查是否是wrap-around误判
                        if (gap > 20000)
                        {
                            _onFrameLossCallback?.Invoke(gap);
                            _lastJumpSource = _nextExpected;
                            _nextExpected = first;
                            _lastJumpTarget = first;
                        }
                        else
                        {
                            var framesInRange = _buffer.Keys
                                .Where(k => 
                                {
                                    int dist = SeqGap(_nextExpected, k);
                                    return dist > 0 && dist < gap;
                                })
                                .ToList();
                            
                            if (framesInRange.Count == 0)
                            {
                                _onFrameLossCallback?.Invoke(gap);
                                _lastJumpSource = _nextExpected;
                                _nextExpected = first;
                                _lastJumpTarget = first;
                            }
                            else
                            {
                                // 分批处理：中间有部分可用帧
                                int maxOutputThisFlush = EXTREME_JUMP_THRESHOLD;
                                int extremeJumpOutputCount = 0;
                                ushort currentIndex = _nextExpected;
                                
                                _onFrameLossCallback?.Invoke(gap);
                                
                                while (extremeJumpOutputCount < maxOutputThisFlush && _buffer.Count > 0)
                                {
                                    var availableFrames = _buffer.Keys
                                        .Where(k => 
                                        {
                                            int dist = SeqGap(currentIndex, k);
                                            return dist > 0 && dist <= (maxOutputThisFlush - extremeJumpOutputCount);
                                        })
                                        .OrderBy(k => SeqGap(currentIndex, k))
                                        .ToList();
                                    
                                    if (availableFrames.Count == 0)
                                    {
                                        _lastJumpSource = currentIndex;
                                        _nextExpected = first;
                                        _lastJumpTarget = first;
                                        break;
                                    }
                                    
                                    ushort nextAvailableValue = availableFrames[0];
                                    
                                    while (currentIndex != nextAvailableValue && extremeJumpOutputCount < maxOutputThisFlush)
                                    {
                                        if (_buffer.TryGetValue(currentIndex, out var data))
                                        {
                                            _buffer.Remove(currentIndex);
                                            output(data);
                                            extremeJumpOutputCount++;
                                        }
                                        currentIndex = (ushort)((currentIndex + 1) & 0xFFFF);
                                    }
                                    
                                    if (currentIndex == nextAvailableValue && _buffer.TryGetValue(nextAvailableValue, out var nextData))
                                    {
                                        _buffer.Remove(nextAvailableValue);
                                        output(nextData);
                                        currentIndex = (ushort)((nextAvailableValue + 1) & 0xFFFF);
                                        extremeJumpOutputCount++;
                                    }
                                }
                                
                                // 更新_nextExpected
                                if (_nextExpected == previousExpected)
                                {
                                    if (extremeJumpOutputCount >= maxOutputThisFlush)
                                    {
                                        _nextExpected = currentIndex;
                                    }
                                    else
                                    {
                                        if (_buffer.Count > 0)
                                        {
                                            var remainingFirst = _buffer.Keys
                                                .OrderBy(k => SeqDistance(k, currentIndex))
                                                .First();
                                            int remainingGap = SeqGap(currentIndex, remainingFirst);
                                            
                                            if (remainingGap > 0 && remainingGap <= MAX_SKIP)
                                            {
                                                _nextExpected = remainingFirst;
                                            }
                                            else if (remainingGap > EXTREME_JUMP_THRESHOLD)
                                            {
                                                _nextExpected = remainingFirst;
                                            }
                                            else
                                            {
                                                _nextExpected = currentIndex;
                                            }
                                        }
                                        else
                                        {
                                            _nextExpected = currentIndex;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // 中等跳帧
                        _onFrameLossCallback?.Invoke(gap);
                        _lastJumpSource = _nextExpected;
                        _nextExpected = first;
                        _lastJumpTarget = first;
                        _jumpBackCount = 0;
                    }
                }
            }
        }

        // 工具函数

        private static int SeqGap(ushort a, ushort b)
            => (b - a + 0x10000) & 0xFFFF;

        private static int SeqDistance(ushort a, ushort b)
            => SeqGap(b, a);

        /// <summary>
        /// RFC1982 比较：a 是否比 b 新
        /// </summary>
        private static bool SeqGreater(ushort a, ushort b)
        {
            int diff = (a - b) & 0xFFFF;
            return diff != 0 && diff < 0x8000;
        }
    }
}
