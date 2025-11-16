using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Congestion
{
    /// <summary>
    /// 带宽估算器 - 使用滑动窗口跟踪网络状况
    /// 参考 chiaki-ng 的 packetstats 实现，但增加了时间窗口和带宽估算
    /// </summary>
    public class BandwidthEstimator
    {
        #region Constants
        
        // 滑动窗口大小（秒）
        private const double WINDOW_SIZE_SECONDS = 2.0;
        
        // 最小窗口样本数（用于稳定估算）
        private const int MIN_SAMPLES = 3;
        
        // 最大窗口样本数（限制内存使用）
        private const int MAX_SAMPLES = 20;
        
        #endregion

        #region Fields
        
        private readonly ILogger<BandwidthEstimator>? _logger;
        private readonly object _lock = new object();
        
        // 滑动窗口：存储时间戳和统计信息
        private readonly Queue<BandwidthSample> _samples = new Queue<BandwidthSample>();
        
        // 当前累积统计（用于快速查询）
        private ulong _totalReceived = 0;
        private ulong _totalLost = 0;
        private DateTime _windowStartTime = DateTime.UtcNow;
        
        // 估算的带宽（字节/秒）
        private double _estimatedBandwidthBps = 0;
        
        // 估算的包丢失率（0.0 - 1.0）
        private double _estimatedLossRate = 0;
        
        #endregion

        #region Constructor
        
        public BandwidthEstimator(ILogger<BandwidthEstimator>? logger = null)
        {
            _logger = logger;
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// 添加一个样本（在收到或丢失包时调用）
        /// </summary>
        public void AddSample(ulong received, ulong lost, DateTime timestamp)
        {
            lock (_lock)
            {
                // 添加新样本
                _samples.Enqueue(new BandwidthSample
                {
                    Received = received,
                    Lost = lost,
                    Timestamp = timestamp
                });
                
                _totalReceived += received;
                _totalLost += lost;
                
                // 清理过期样本（超出时间窗口）
                CleanupOldSamples(timestamp);
                
                // 更新估算值
                UpdateEstimates(timestamp);
            }
        }
        
        /// <summary>
        /// 获取当前带宽估算（字节/秒）
        /// </summary>
        public double GetEstimatedBandwidthBps()
        {
            lock (_lock)
            {
                return _estimatedBandwidthBps;
            }
        }
        
        /// <summary>
        /// 获取当前包丢失率估算（0.0 - 1.0）
        /// </summary>
        public double GetEstimatedLossRate()
        {
            lock (_lock)
            {
                return _estimatedLossRate;
            }
        }
        
        /// <summary>
        /// 获取窗口内的统计信息
        /// </summary>
        public (ulong received, ulong lost, double lossRate) GetWindowStats()
        {
            lock (_lock)
            {
                ulong total = _totalReceived + _totalLost;
                double lossRate = total > 0 ? (double)_totalLost / total : 0.0;
                return (_totalReceived, _totalLost, lossRate);
            }
        }
        
        /// <summary>
        /// 重置所有统计信息
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _samples.Clear();
                _totalReceived = 0;
                _totalLost = 0;
                _windowStartTime = DateTime.UtcNow;
                _estimatedBandwidthBps = 0;
                _estimatedLossRate = 0;
            }
        }
        
        #endregion

        #region Private Methods
        
        /// <summary>
        /// 清理超出时间窗口的旧样本
        /// </summary>
        private void CleanupOldSamples(DateTime currentTime)
        {
            while (_samples.Count > 0)
            {
                var oldest = _samples.Peek();
                var age = (currentTime - oldest.Timestamp).TotalSeconds;
                
                if (age <= WINDOW_SIZE_SECONDS)
                    break;
                
                // 移除过期样本
                var removed = _samples.Dequeue();
                _totalReceived -= removed.Received;
                _totalLost -= removed.Lost;
            }
        }
        
        /// <summary>
        /// 更新带宽和丢失率估算
        /// </summary>
        private void UpdateEstimates(DateTime currentTime)
        {
            if (_samples.Count < MIN_SAMPLES)
            {
                // 样本不足，使用默认值
                _estimatedBandwidthBps = 0;
                _estimatedLossRate = 0;
                return;
            }
            
            // 计算窗口时间跨度
            var windowSpan = (currentTime - _windowStartTime).TotalSeconds;
            if (windowSpan < 0.1) // 至少 100ms
            {
                windowSpan = 0.1;
            }
            
            // 估算带宽（假设平均包大小为 1400 字节，这是典型的 UDP 包大小）
            const double AVERAGE_PACKET_SIZE = 1400.0;
            var totalPackets = _totalReceived + _totalLost;
            _estimatedBandwidthBps = (totalPackets * AVERAGE_PACKET_SIZE) / windowSpan;
            
            // 估算丢失率
            var total = _totalReceived + _totalLost;
            _estimatedLossRate = total > 0 ? (double)_totalLost / total : 0.0;
            
            // 限制样本数量（防止内存泄漏）
            while (_samples.Count > MAX_SAMPLES)
            {
                var removed = _samples.Dequeue();
                _totalReceived -= removed.Received;
                _totalLost -= removed.Lost;
            }
        }
        
        #endregion

        #region Helper Classes
        
        private struct BandwidthSample
        {
            public ulong Received;
            public ulong Lost;
            public DateTime Timestamp;
        }
        
        #endregion
    }
}

