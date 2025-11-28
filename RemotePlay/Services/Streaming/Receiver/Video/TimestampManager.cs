using System;
using System.Collections.Concurrent;
using System.Linq;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 时间戳管理器 - 线程安全
    /// 负责时间戳计算和帧率检测
    /// </summary>
    internal class TimestampManager
    {
        private readonly object _lock = new();
        
        private uint _timestamp = 0;
        private DateTime _lastFrameTime = DateTime.MinValue;
        
        // 动态帧率检测
        private double _detectedFrameRate = 60.0;
        private double _timestampIncrement = 1500.0; // 90000 / 60
        private readonly ConcurrentQueue<double> _frameIntervalHistory = new();
        
        // 常量
        private const uint VIDEO_CLOCK_RATE = 90000;
        private const double DEFAULT_FRAME_RATE = 60.0;
        private const int FRAME_RATE_HISTORY_SIZE = 30;
        private const double MIN_FRAME_RATE = 15.0;
        private const double MAX_FRAME_RATE = 120.0;
        private const int FRAME_RATE_UPDATE_INTERVAL_MS = 500;
        
        private DateTime _lastFrameRateUpdateTime = DateTime.MinValue;

        /// <summary>
        /// 获取下一个时间戳
        /// </summary>
        public uint GetNextTimestamp(DateTime frameTime)
        {
            lock (_lock)
            {
                UpdateFrameRate(frameTime);
                
                if (_lastFrameTime != DateTime.MinValue)
                {
                    var elapsed = (frameTime - _lastFrameTime).TotalSeconds;
                    if (elapsed > 0 && elapsed < 1.0)
                    {
                        // 正常情况：基于实际时间间隔计算
                        _timestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                    }
                    else
                    {
                        // 异常情况：使用动态检测的帧率计算增量
                        _timestamp += (uint)_timestampIncrement;
                    }
                }
                
                _lastFrameTime = frameTime;
                
                // 处理时间戳回绕
                if (_timestamp > 0xFFFFFFFF - VIDEO_CLOCK_RATE)
                {
                    _timestamp = 0;
                }
                
                return _timestamp;
            }
        }

        /// <summary>
        /// 更新帧率检测
        /// </summary>
        private void UpdateFrameRate(DateTime frameTime)
        {
            if (_lastFrameTime == DateTime.MinValue)
            {
                return;
            }
            
            var elapsed = (frameTime - _lastFrameTime).TotalSeconds;
            
            if (elapsed > 0 && elapsed < 1.0)
            {
                _frameIntervalHistory.Enqueue(elapsed);
                
                // 保持历史记录在合理大小
                while (_frameIntervalHistory.Count > FRAME_RATE_HISTORY_SIZE)
                {
                    _frameIntervalHistory.TryDequeue(out _);
                }
                
                // 定期更新帧率
                var now = DateTime.UtcNow;
                if (_lastFrameRateUpdateTime == DateTime.MinValue || 
                    (now - _lastFrameRateUpdateTime).TotalMilliseconds >= FRAME_RATE_UPDATE_INTERVAL_MS)
                {
                    if (_frameIntervalHistory.Count >= 5)
                    {
                        // 计算平均帧间隔
                        var intervals = _frameIntervalHistory.ToArray();
                        double avgInterval = intervals.Average();
                        
                        // 计算帧率
                        double newFrameRate = 1.0 / avgInterval;
                        newFrameRate = Math.Max(MIN_FRAME_RATE, Math.Min(MAX_FRAME_RATE, newFrameRate));
                        
                        // 平滑更新
                        _detectedFrameRate = _detectedFrameRate * 0.7 + newFrameRate * 0.3;
                        
                        // 重新计算时间戳增量
                        _timestampIncrement = VIDEO_CLOCK_RATE / _detectedFrameRate;
                        
                        _lastFrameRateUpdateTime = now;
                    }
                }
            }
        }

        /// <summary>
        /// 检测到的帧率
        /// </summary>
        public double DetectedFrameRate
        {
            get
            {
                lock (_lock)
                {
                    return _detectedFrameRate;
                }
            }
        }

        /// <summary>
        /// 当前时间戳
        /// </summary>
        public uint CurrentTimestamp
        {
            get
            {
                lock (_lock)
                {
                    return _timestamp;
                }
            }
        }
    }
}

