using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services
{
    /// <summary>
    /// 延时统计服务 - 跟踪从PS5到浏览器的端到端延迟
    /// </summary>
    public class LatencyStatisticsService
    {
        private readonly ILogger<LatencyStatisticsService> _logger;
        
        // 存储每个会话的延时统计
        private readonly ConcurrentDictionary<string, SessionLatencyStats> _sessionStats;
        
        public LatencyStatisticsService(ILogger<LatencyStatisticsService> logger)
        {
            _logger = logger;
            _sessionStats = new ConcurrentDictionary<string, SessionLatencyStats>();
        }
        
        /// <summary>
        /// 记录数据包到达时间（PS5 -> 服务器）
        /// </summary>
        public void RecordPacketArrival(string sessionId, string packetType, long frameIndex = 0)
        {
            var stats = _sessionStats.GetOrAdd(sessionId, _ => new SessionLatencyStats(sessionId));
            var timestamp = DateTime.UtcNow;
            
            stats.RecordArrival(packetType, frameIndex, timestamp);
        }
        
        /// <summary>
        /// 记录数据包处理时间（AVHandler处理）
        /// </summary>
        public void RecordPacketProcessed(string sessionId, string packetType, long frameIndex = 0)
        {
            if (_sessionStats.TryGetValue(sessionId, out var stats))
            {
                stats.RecordProcessed(packetType, frameIndex, DateTime.UtcNow);
            }
        }
        
        /// <summary>
        /// 记录数据包发送时间（服务器 -> WebRTC）
        /// </summary>
        public void RecordPacketSent(string sessionId, string packetType, long frameIndex = 0)
        {
            if (_sessionStats.TryGetValue(sessionId, out var stats))
            {
                stats.RecordSent(packetType, frameIndex, DateTime.UtcNow);
            }
        }
        
        /// <summary>
        /// 记录数据包接收时间（浏览器接收）
        /// </summary>
        public void RecordPacketReceived(string sessionId, string packetType, long frameIndex, DateTime clientReceiveTime)
        {
            if (_sessionStats.TryGetValue(sessionId, out var stats))
            {
                stats.RecordReceived(packetType, frameIndex, clientReceiveTime);
            }
        }
        
        /// <summary>
        /// 获取会话的延时统计
        /// </summary>
        public SessionLatencyStats? GetStats(string sessionId)
        {
            _sessionStats.TryGetValue(sessionId, out var stats);
            return stats;
        }
        
        /// <summary>
        /// 清理会话统计
        /// </summary>
        public void RemoveSession(string sessionId)
        {
            _sessionStats.TryRemove(sessionId, out _);
        }
        
        /// <summary>
        /// 获取所有会话的统计摘要
        /// </summary>
        public Dictionary<string, LatencySummary> GetAllSummaries()
        {
            var summaries = new Dictionary<string, LatencySummary>();
            foreach (var kvp in _sessionStats)
            {
                summaries[kvp.Key] = kvp.Value.GetSummary();
            }
            return summaries;
        }
    }
    
    /// <summary>
    /// 会话延时统计
    /// </summary>
    public class SessionLatencyStats
    {
        private readonly string _sessionId;
        private readonly ConcurrentDictionary<long, PacketLatencyData> _packetData;
        private readonly object _lock = new object();
        
        // 统计信息
        private readonly Queue<double> _serverProcessingTimes = new Queue<double>(); // 服务器处理时间
        private readonly Queue<double> _networkTransmitTimes = new Queue<double>(); // 网络传输时间
        private readonly Queue<double> _totalLatencyTimes = new Queue<double>(); // 总延迟时间
        private const int MaxSamples = 1000; // 保留最近1000个样本
        
        // ✅ 当前延迟（最新一次计算的实时延迟值，与PS5的端到端延迟）
        // 这是从PS5画面产生到浏览器显示的完整延迟
        private double _latestLatency = 0; // 最新计算的延迟值（毫秒）
        private DateTime _latestLatencyTimestamp = DateTime.MinValue; // 最新延迟的时间戳
        
        public SessionLatencyStats(string sessionId)
        {
            _sessionId = sessionId;
            _packetData = new ConcurrentDictionary<long, PacketLatencyData>();
        }
        
        public void RecordArrival(string packetType, long frameIndex, DateTime timestamp)
        {
            var data = _packetData.GetOrAdd(frameIndex, _ => new PacketLatencyData
            {
                FrameIndex = frameIndex,
                PacketType = packetType
            });
            
            lock (_lock)
            {
                data.ArrivalTime = timestamp;
            }
        }
        
        public void RecordProcessed(string packetType, long frameIndex, DateTime timestamp)
        {
            if (_packetData.TryGetValue(frameIndex, out var data))
            {
                lock (_lock)
                {
                    data.ProcessedTime = timestamp;
                    
                    // 计算服务器处理时间
                    if (data.ArrivalTime.HasValue)
                    {
                        var processingTime = (timestamp - data.ArrivalTime.Value).TotalMilliseconds;
                        AddSample(_serverProcessingTimes, processingTime);
                    }
                }
            }
        }
        
        public void RecordSent(string packetType, long frameIndex, DateTime timestamp)
        {
            if (_packetData.TryGetValue(frameIndex, out var data))
            {
                lock (_lock)
                {
                    data.SentTime = timestamp;
                }
            }
        }
        
        public void RecordReceived(string packetType, long frameIndex, DateTime clientReceiveTime)
        {
            lock (_lock)
            {
                PacketLatencyData? data = null;
                
                // ✅ 修复：优先使用精确的 frameIndex 匹配
                if (_packetData.TryGetValue(frameIndex, out var exactMatch))
                {
                    data = exactMatch;
                }
                else
                {
                    // ✅ 如果找不到精确匹配，使用时间窗口匹配最近的数据包（在1秒内）
                    // 这样可以处理 frameIndex 不匹配的情况（例如服务器和客户端计数方式不同）
                    var bestMatch = _packetData.Values
                        .Where(d => d.PacketType == packetType && 
                                   d.SentTime.HasValue &&
                                   Math.Abs((clientReceiveTime - d.SentTime.Value).TotalMilliseconds) < 1000)
                        .OrderBy(d => Math.Abs((clientReceiveTime - (d.SentTime ?? DateTime.MinValue)).TotalMilliseconds))
                        .FirstOrDefault();
                    
                    if (bestMatch != null)
                    {
                        data = bestMatch;
                    }
                }
                
                if (data != null)
                {
                    data.ReceivedTime = clientReceiveTime;
                    
                    // 计算网络传输时间（从服务器发送到客户端接收）
                    if (data.SentTime.HasValue)
                    {
                        var networkTime = (clientReceiveTime - data.SentTime.Value).TotalMilliseconds;
                        // ✅ 过滤异常值：如果网络传输时间在合理范围内（0-5000ms），才记录
                        if (networkTime >= 0 && networkTime < 5000)
                        {
                            AddSample(_networkTransmitTimes, networkTime);
                        }
                    }
                    
                    // ✅ 计算端到端延迟：从PS5画面产生到浏览器接收的延迟（已减去缓冲延迟）
                    // ArrivalTime: PS5数据包到达服务器的时间（接近PS5画面产生时间）
                    // clientReceiveTime: 客户端数据包接收时间（渲染时间 - 缓冲延迟，不包含缓冲累积）
                    // 延迟 = 客户端接收时间 - PS5到达服务器时间
                    // 这包含了：PS5->服务器网络、服务器处理、服务器->客户端网络、客户端解码
                    // 注意：不包含客户端缓冲延迟（已减去），反映真实的网络和处理延迟
                    if (data.ArrivalTime.HasValue)
                    {
                        var totalTime = (clientReceiveTime - data.ArrivalTime.Value).TotalMilliseconds;
                        // ✅ 过滤异常值：如果总延迟在合理范围内（0-10000ms），才记录
                        if (totalTime >= 0 && totalTime < 10000)
                        {
                            AddSample(_totalLatencyTimes, totalTime);
                            
                            // ✅ 更新当前延迟（最新一次计算的实时值，PS5画面到浏览器显示的延迟）
                            _latestLatency = totalTime;
                            _latestLatencyTimestamp = clientReceiveTime;
                        }
                    }
                }
                
                // 清理旧数据（保留最近100个）
                if (_packetData.Count > 100)
                {
                    var oldestFrame = _packetData.Keys.OrderBy(k => k).First();
                    _packetData.TryRemove(oldestFrame, out _);
                }
            }
        }
        
        private void AddSample(Queue<double> queue, double value)
        {
            queue.Enqueue(value);
            if (queue.Count > MaxSamples)
            {
                queue.Dequeue();
            }
        }
        
        public LatencySummary GetSummary()
        {
            lock (_lock)
            {
                // ✅ 获取当前延迟（最新一次计算的实时值，不是累加的）
                double currentLatency = 0;
                var now = DateTime.UtcNow;
                
                // 如果最新延迟时间戳在3秒内，认为是有效的实时当前延迟
                if (_latestLatencyTimestamp != DateTime.MinValue && 
                    (now - _latestLatencyTimestamp).TotalSeconds < 3)
                {
                    // ✅ 直接使用最新计算的延迟值（实时值，不是累加的）
                    currentLatency = _latestLatency;
                }
                else if (_totalLatencyTimes.Count > 0)
                {
                    // 备用方案：如果最新值过期（超过3秒），使用队列中最近的值
                    // 获取队列中最后一个值（最近添加的）
                    var timesArray = _totalLatencyTimes.ToArray();
                    if (timesArray.Length > 0)
                    {
                        // 使用队列中最后一个值（最近计算的）
                        currentLatency = timesArray[timesArray.Length - 1];
                    }
                }
                
                return new LatencySummary
                {
                    SessionId = _sessionId,
                    CurrentLatency = currentLatency, // ✅ 当前延迟（与PS5的延迟，最新值）
                    ServerProcessingAvg = _serverProcessingTimes.Count > 0 ? _serverProcessingTimes.Average() : 0,
                    ServerProcessingMin = _serverProcessingTimes.Count > 0 ? _serverProcessingTimes.Min() : 0,
                    ServerProcessingMax = _serverProcessingTimes.Count > 0 ? _serverProcessingTimes.Max() : 0,
                    NetworkTransmitAvg = _networkTransmitTimes.Count > 0 ? _networkTransmitTimes.Average() : 0,
                    NetworkTransmitMin = _networkTransmitTimes.Count > 0 ? _networkTransmitTimes.Min() : 0,
                    NetworkTransmitMax = _networkTransmitTimes.Count > 0 ? _networkTransmitTimes.Max() : 0,
                    TotalLatencyAvg = _totalLatencyTimes.Count > 0 ? _totalLatencyTimes.Average() : 0,
                    TotalLatencyMin = _totalLatencyTimes.Count > 0 ? _totalLatencyTimes.Min() : 0,
                    TotalLatencyMax = _totalLatencyTimes.Count > 0 ? _totalLatencyTimes.Max() : 0,
                    SampleCount = _totalLatencyTimes.Count
                };
            }
        }
    }
    
    /// <summary>
    /// 数据包延时数据
    /// </summary>
    public class PacketLatencyData
    {
        public long FrameIndex { get; set; }
        public string PacketType { get; set; } = "";
        public DateTime? ArrivalTime { get; set; }
        public DateTime? ProcessedTime { get; set; }
        public DateTime? SentTime { get; set; }
        public DateTime? ReceivedTime { get; set; }
    }
    
    /// <summary>
    /// 延时统计摘要
    /// </summary>
    public class LatencySummary
    {
        public string SessionId { get; set; } = "";
        
        // ✅ 当前延迟（与PS5的端到端延迟，毫秒）
        public double CurrentLatency { get; set; }
        
        // 服务器处理时间（毫秒）
        public double ServerProcessingAvg { get; set; }
        public double ServerProcessingMin { get; set; }
        public double ServerProcessingMax { get; set; }
        
        // 网络传输时间（毫秒）
        public double NetworkTransmitAvg { get; set; }
        public double NetworkTransmitMin { get; set; }
        public double NetworkTransmitMax { get; set; }
        
        // 总延迟时间（毫秒）
        public double TotalLatencyAvg { get; set; }
        public double TotalLatencyMin { get; set; }
        public double TotalLatencyMax { get; set; }
        
        public int SampleCount { get; set; }
    }
}

