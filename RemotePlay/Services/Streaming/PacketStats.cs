namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 包统计系统 - 跟踪所有收发包的详细统计信息
    /// </summary>
    public class PacketStats
    {
        #region 统计数据
        
        private readonly object _lock = new object();
        
        // 接收统计
        private ulong _totalReceived = 0;          // 总接收包数
        private ulong _totalLost = 0;              // 总丢失包数
        private ulong _totalDuplicate = 0;         // 重复包数
        private ulong _totalOutOfOrder = 0;        // 乱序包数
        
        // 发送统计
        private ulong _totalSent = 0;              // 总发送包数
        private ulong _feedbackSent = 0;           // Feedback 包数
        private ulong _congestionSent = 0;         // Congestion 包数
        
        // 包类型统计
        private ulong _videoPackets = 0;           // 视频包数
        private ulong _audioPackets = 0;           // 音频包数
        private ulong _controlPackets = 0;         // 控制包数
        
        // 字节统计
        private ulong _totalBytesReceived = 0;     // 总接收字节数
        private ulong _totalBytesSent = 0;         // 总发送字节数
        
        // 序列号跟踪
        private uint _lastReceivedTsn = 0;         // 最后接收的 TSN
        private uint _expectedTsn = 1;             // 期望的下一个 TSN
        
        // 时间戳
        private DateTime _startTime;
        private DateTime _lastReportTime;
        
        #endregion
        
        #region Constructor
        
        public PacketStats()
        {
            _startTime = DateTime.UtcNow;
            _lastReportTime = _startTime;
        }
        
        #endregion
        
        #region 接收统计
        
        /// <summary>
        /// 记录接收到的包
        /// </summary>
        /// <param name="tsn">包的序列号</param>
        /// <param name="bytes">包的字节数</param>
        /// <param name="packetType">包类型</param>
        /// <returns>是否为乱序包</returns>
        public bool RecordReceived(uint tsn, int bytes, PacketType packetType = PacketType.Unknown)
        {
            lock (_lock)
            {
                _totalReceived++;
                _totalBytesReceived += (ulong)bytes;
                
                // 更新包类型统计
                switch (packetType)
                {
                    case PacketType.Video:
                        _videoPackets++;
                        break;
                    case PacketType.Audio:
                        _audioPackets++;
                        break;
                    case PacketType.Control:
                        _controlPackets++;
                        break;
                }
                
                // 检查序列号
                bool isOutOfOrder = false;
                
                if (tsn < _expectedTsn)
                {
                    // 乱序或重复包
                    if (tsn < _lastReceivedTsn)
                    {
                        _totalOutOfOrder++;
                        isOutOfOrder = true;
                    }
                    else
                    {
                        _totalDuplicate++;
                    }
                }
                else if (tsn > _expectedTsn)
                {
                    // 有丢包
                    uint gap = tsn - _expectedTsn;
                    _totalLost += gap;
                    _expectedTsn = tsn + 1;
                    isOutOfOrder = (gap > 1);  // 如果跳过多个包，标记为乱序
                    
                    if (isOutOfOrder)
                    {
                        _totalOutOfOrder++;
                    }
                }
                else
                {
                    // 正常顺序
                    _expectedTsn = tsn + 1;
                }
                
                _lastReceivedTsn = tsn;
                
                return isOutOfOrder;
            }
        }
        
        /// <summary>
        /// 记录丢包
        /// </summary>
        public void RecordLost(uint count = 1)
        {
            lock (_lock)
            {
                _totalLost += count;
            }
        }
        
        #endregion
        
        #region 发送统计
        
        /// <summary>
        /// 记录发送的包
        /// </summary>
        public void RecordSent(int bytes, PacketSendType type = PacketSendType.Normal)
        {
            lock (_lock)
            {
                _totalSent++;
                _totalBytesSent += (ulong)bytes;
                
                switch (type)
                {
                    case PacketSendType.Feedback:
                        _feedbackSent++;
                        break;
                    case PacketSendType.Congestion:
                        _congestionSent++;
                        break;
                }
            }
        }
        
        #endregion
        
        #region 查询方法
        
        /// <summary>
        /// 获取当前统计快照
        /// </summary>
        public StatsSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var totalElapsed = (now - _startTime).TotalSeconds;
                var sinceLastReport = (now - _lastReportTime).TotalSeconds;
                
                var snapshot = new StatsSnapshot
                {
                    // 接收统计
                    TotalReceived = _totalReceived,
                    TotalLost = _totalLost,
                    TotalDuplicate = _totalDuplicate,
                    TotalOutOfOrder = _totalOutOfOrder,
                    
                    // 发送统计
                    TotalSent = _totalSent,
                    FeedbackSent = _feedbackSent,
                    CongestionSent = _congestionSent,
                    
                    // 包类型统计
                    VideoPackets = _videoPackets,
                    AudioPackets = _audioPackets,
                    ControlPackets = _controlPackets,
                    
                    // 字节统计
                    TotalBytesReceived = _totalBytesReceived,
                    TotalBytesSent = _totalBytesSent,
                    
                    // 速率计算
                    ElapsedSeconds = totalElapsed,
                    ReceiveRatePacketsPerSec = totalElapsed > 0 ? _totalReceived / totalElapsed : 0,
                    ReceiveRateMbps = totalElapsed > 0 ? (_totalBytesReceived * 8) / (totalElapsed * 1_000_000) : 0,
                    SendRatePacketsPerSec = totalElapsed > 0 ? _totalSent / totalElapsed : 0,
                    SendRateMbps = totalElapsed > 0 ? (_totalBytesSent * 8) / (totalElapsed * 1_000_000) : 0,
                    
                    // 丢包率
                    LossRate = _totalReceived > 0 ? (double)_totalLost / (_totalReceived + _totalLost) : 0,
                    
                    // 时间
                    Timestamp = now
                };
                
                _lastReportTime = now;
                
                return snapshot;
            }
        }
        
        /// <summary>
        /// 获取接收/丢失包数（用于 Congestion 报告）
        /// </summary>
        public (ushort received, ushort lost) GetReceivedAndLost()
        {
            lock (_lock)
            {
                // 注意：这里返回的是从上次调用以来的增量，而不是总量
                // 但为了简化，我们返回总量的低 16 位
                // 如果需要增量，应该在调用后重置计数器
                
                ushort received = (ushort)Math.Min(_totalReceived, ushort.MaxValue);
                ushort lost = (ushort)Math.Min(_totalLost, ushort.MaxValue);
                
                return (received, lost);
            }
        }
        
        /// <summary>
        /// 重置统计信息
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _totalReceived = 0;
                _totalLost = 0;
                _totalDuplicate = 0;
                _totalOutOfOrder = 0;
                _totalSent = 0;
                _feedbackSent = 0;
                _congestionSent = 0;
                _videoPackets = 0;
                _audioPackets = 0;
                _controlPackets = 0;
                _totalBytesReceived = 0;
                _totalBytesSent = 0;
                _lastReceivedTsn = 0;
                _expectedTsn = 1;
                _startTime = DateTime.UtcNow;
                _lastReportTime = _startTime;
            }
        }
        
        #endregion
    }
    
    /// <summary>
    /// 统计快照 - 某一时刻的统计数据
    /// </summary>
    public class StatsSnapshot
    {
        // 接收统计
        public ulong TotalReceived { get; set; }
        public ulong TotalLost { get; set; }
        public ulong TotalDuplicate { get; set; }
        public ulong TotalOutOfOrder { get; set; }
        
        // 发送统计
        public ulong TotalSent { get; set; }
        public ulong FeedbackSent { get; set; }
        public ulong CongestionSent { get; set; }
        
        // 包类型统计
        public ulong VideoPackets { get; set; }
        public ulong AudioPackets { get; set; }
        public ulong ControlPackets { get; set; }
        
        // 字节统计
        public ulong TotalBytesReceived { get; set; }
        public ulong TotalBytesSent { get; set; }
        
        // 速率统计
        public double ElapsedSeconds { get; set; }
        public double ReceiveRatePacketsPerSec { get; set; }
        public double ReceiveRateMbps { get; set; }
        public double SendRatePacketsPerSec { get; set; }
        public double SendRateMbps { get; set; }
        
        // 质量指标
        public double LossRate { get; set; }
        
        // 时间戳
        public DateTime Timestamp { get; set; }
        
        /// <summary>
        /// 格式化为日志字符串
        /// </summary>
        public override string ToString()
        {
            return $"📊 Stats: RX={TotalReceived} ({ReceiveRateMbps:F2} Mbps), " +
                   $"Lost={TotalLost} ({LossRate:P2}), " +
                   $"Video={VideoPackets}, Audio={AudioPackets}, " +
                   $"TX={TotalSent} (FB={FeedbackSent}, CG={CongestionSent})";
        }
    }
    
    /// <summary>
    /// 包类型
    /// </summary>
    public enum PacketType
    {
        Unknown = 0,
        Video = 1,
        Audio = 2,
        Control = 3
    }
    
    /// <summary>
    /// 发送包类型
    /// </summary>
    public enum PacketSendType
    {
        Normal = 0,
        Feedback = 1,
        Congestion = 2
    }
}

