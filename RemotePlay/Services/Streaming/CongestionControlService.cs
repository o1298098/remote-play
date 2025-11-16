using System.Buffers.Binary;
using RemotePlay.Services.Streaming.Congestion;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 拥塞控制服务 - 定期向主机报告网络统计信息
    /// 让主机能够动态调整码率和质量
    /// 
    /// ✅ 增强功能（参考 chiaki-ng）：
    /// 1. 滑动窗口带宽估算：使用时间窗口跟踪网络状况
    /// 2. 自适应上报频率：根据网络状况动态调整上报频率
    /// </summary>
    public class CongestionControlService : IDisposable
    {
        #region Constants
        
        // ✅ 基础上报间隔（参考 chiaki-ng: 200ms）
        private const int BASE_SEND_INTERVAL_MS = 200;
        
        // ✅ 最小上报间隔（网络状况差时）
        private const int MIN_SEND_INTERVAL_MS = 100;
        
        // ✅ 最大上报间隔（网络状况好时）
        private const int MAX_SEND_INTERVAL_MS = 500;
        
        // ✅ 丢失率阈值（超过此值认为网络状况差）
        private const double HIGH_LOSS_THRESHOLD = 0.05; // 5%
        
        // ✅ 丢失率阈值（低于此值认为网络状况好）
        private const double LOW_LOSS_THRESHOLD = 0.01; // 1%
        
        private const int CONGESTION_PACKET_SIZE = 15;  // 0x0f bytes
        
        #endregion

        #region Fields
        
        private readonly ILogger<CongestionControlService> _logger;
        private readonly Func<byte[], Task> _sendRawFunc;  // 发送原始包的回调
        private readonly Func<ulong> _getKeyPosFunc;       // 获取 key_pos 的回调
        private readonly Func<(ushort, ushort)>? _getPacketStatsFunc;  // 获取包统计的回调（可选）
        
        // ✅ 带宽估算器（滑动窗口）
        private readonly BandwidthEstimator _bandwidthEstimator;
        
        private CancellationTokenSource? _cts;
        private Task? _congestionLoop;
        
        private ushort _sequenceNumber = 0;
        private ushort _packetsReceived = 0;
        private ushort _packetsLost = 0;
        
        private readonly object _statsLock = new object();
        private (ushort received, ushort lost)? _overrideSample;
        private bool _sustainedCongestionMode = false; // ✅ 持续拥塞模式（用于触发被动降档）
        private (ushort received, ushort lost) _sustainedCongestionSample = (5, 5); // 默认高丢失样本
        
        // ✅ 当前自适应上报间隔
        private int _currentSendIntervalMs = BASE_SEND_INTERVAL_MS;
        
        private bool _isRunning = false;
        
        #endregion

        #region Constructor & Lifecycle
        
        /// <summary>
        /// 创建拥塞控制服务
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="sendRawFunc">发送原始包的回调函数</param>
        /// <param name="getKeyPosFunc">获取当前 key_pos 的回调函数</param>
        /// <param name="getPacketStatsFunc">获取包统计的回调函数（可选）</param>
        public CongestionControlService(
            ILogger<CongestionControlService> logger,
            Func<byte[], Task> sendRawFunc,
            Func<ulong> getKeyPosFunc,
            Func<(ushort, ushort)>? getPacketStatsFunc = null)
        {
            _logger = logger;
            _sendRawFunc = sendRawFunc;
            _getKeyPosFunc = getKeyPosFunc;
            _getPacketStatsFunc = getPacketStatsFunc;
            
            // ✅ 初始化带宽估算器（使用 null logger，因为 BandwidthEstimator 的日志是可选的）
            _bandwidthEstimator = new BandwidthEstimator(null);
        }
        
        /// <summary>
        /// 启动拥塞控制循环
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                _logger.LogWarning("CongestionControl already running");
                return;
            }
            
            _cts = new CancellationTokenSource();
            _congestionLoop = Task.Run(() => CongestionLoopAsync(_cts.Token), _cts.Token);
            _isRunning = true;
            
            _logger.LogInformation("✅ CongestionControl started - adaptive interval (base={BaseMs}ms, range={MinMs}-{MaxMs}ms)", 
                BASE_SEND_INTERVAL_MS, MIN_SEND_INTERVAL_MS, MAX_SEND_INTERVAL_MS);
        }
        
        /// <summary>
        /// 停止拥塞控制循环
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;
            
            _cts?.Cancel();
            
            if (_congestionLoop != null)
            {
                try
                {
                    await _congestionLoop;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            
            _isRunning = false;
            _logger.LogInformation("CongestionControl stopped");
        }
        
        public void Dispose()
        {
            StopAsync().Wait();
            _cts?.Dispose();
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// 报告收到一个包（用于统计）
        /// </summary>
        public void ReportPacketReceived()
        {
            lock (_statsLock)
            {
                _packetsReceived++;
            }
            
            // ✅ 更新带宽估算器
            _bandwidthEstimator.AddSample(1, 0, DateTime.UtcNow);
        }
        
        /// <summary>
        /// 报告丢失一个包（用于统计）
        /// </summary>
        public void ReportPacketLost()
        {
            lock (_statsLock)
            {
                _packetsLost++;
            }
            
            // ✅ 更新带宽估算器
            _bandwidthEstimator.AddSample(0, 1, DateTime.UtcNow);
        }
        
        /// <summary>
        /// 重置统计信息
        /// </summary>
        public void ResetStats()
        {
            lock (_statsLock)
            {
                _packetsReceived = 0;
                _packetsLost = 0;
            }
            
            // ✅ 重置带宽估算器
            _bandwidthEstimator.Reset();
        }
        
        #endregion

        #region Congestion Loop
        
        /// <summary>
        /// 拥塞控制主循环
        /// ✅ 使用自适应上报频率（根据网络状况动态调整）
        /// </summary>
        private async Task CongestionLoopAsync(CancellationToken ct)
        {
            _logger.LogInformation("🔄 CongestionControl loop started (adaptive interval)");
            
            int packetCount = 0;
            var startTime = DateTime.UtcNow;
            var lastStatsTime = DateTime.UtcNow;
            ushort lastReceived = 0;
            ushort lastLost = 0;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ✅ 自适应等待间隔
                    await Task.Delay(_currentSendIntervalMs, ct);
                    
                    var currentTime = DateTime.UtcNow;
                    
                    // 获取当前统计信息
                    ushort seqNum;
                    ushort received;
                    ushort lost;
                    
                    lock (_statsLock)
                    {
                        seqNum = _sequenceNumber++;
                        
                        // ✅ 如果有包统计回调，使用它；否则使用内部计数器
                        if (_getPacketStatsFunc != null)
                        {
                            (received, lost) = _getPacketStatsFunc();
                        }
                        else
                        {
                            received = _packetsReceived;
                            lost = _packetsLost;
                        }

                        // ✅ 优先使用一次性覆盖样本，否则使用持续拥塞模式
                        if (_overrideSample.HasValue)
                        {
                            received = _overrideSample.Value.received;
                            lost = _overrideSample.Value.lost;
                            _overrideSample = null;
                        }
                        else if (_sustainedCongestionMode)
                        {
                            // ✅ 持续拥塞模式：持续报告高丢失以触发主机被动降档
                            received = _sustainedCongestionSample.received;
                            lost = _sustainedCongestionSample.lost;
                        }
                    }
                    
                    // ✅ 更新带宽估算器（使用增量统计）
                    var deltaReceived = (ulong)(received >= lastReceived ? received - lastReceived : received + (ushort.MaxValue - lastReceived));
                    var deltaLost = (ulong)(lost >= lastLost ? lost - lastLost : lost + (ushort.MaxValue - lastLost));
                    _bandwidthEstimator.AddSample(deltaReceived, deltaLost, currentTime);
                    lastReceived = received;
                    lastLost = lost;
                    lastStatsTime = currentTime;
                    
                    // ✅ 根据带宽估算调整上报频率
                    UpdateAdaptiveInterval();
                    
                    // 构造并发送拥塞包
                    var packet = BuildCongestionPacket(seqNum, received, lost);
                    await _sendRawFunc(packet);
                    
                    packetCount++;
                    
                    // ✅ 前5个包记录详细统计
                    if (packetCount <= 5)
                    {
                        string mode = _sustainedCongestionMode ? " [SUSTAINED CONGESTION]" : "";
                        var bandwidthMbps = _bandwidthEstimator.GetEstimatedBandwidthBps() / (1024.0 * 1024.0);
                        var lossRate = _bandwidthEstimator.GetEstimatedLossRate() * 100.0;
                        _logger.LogInformation("📊 Congestion #{Num}: received={Received}, lost={Lost}, seqNum={Seq}, " +
                            "bandwidth={Bandwidth:F2}Mbps, lossRate={LossRate:F2}%, interval={Interval}ms{Mode}",
                            packetCount, received, lost, seqNum, bandwidthMbps, lossRate, _currentSendIntervalMs, mode);
                    }
                    // 定期日志（每 30 秒）
                    else if (packetCount % 150 == 0) // 约每 30 秒（150 * 200ms）
                    {
                        var elapsed = (currentTime - startTime).TotalSeconds;
                        var rate = packetCount / elapsed;
                        string mode = _sustainedCongestionMode ? " [SUSTAINED CONGESTION]" : "";
                        var bandwidthMbps = _bandwidthEstimator.GetEstimatedBandwidthBps() / (1024.0 * 1024.0);
                        var lossRate = _bandwidthEstimator.GetEstimatedLossRate() * 100.0;
                        
                        _logger.LogInformation("📊 CongestionControl: sent {Count} packets ({Rate:F1}/s), " +
                            "stats: received={Received}, lost={Lost}, bandwidth={Bandwidth:F2}Mbps, " +
                            "lossRate={LossRate:F2}%, interval={Interval}ms{Mode}",
                            packetCount, rate, received, lost, bandwidthMbps, lossRate, _currentSendIntervalMs, mode);
                    }
                    // ✅ 持续拥塞模式：每 25 包记录一次（约每 5 秒）以便观察
                    else if (_sustainedCongestionMode && packetCount % 25 == 0)
                    {
                        var bandwidthMbps = _bandwidthEstimator.GetEstimatedBandwidthBps() / (1024.0 * 1024.0);
                        var lossRate = _bandwidthEstimator.GetEstimatedLossRate() * 100.0;
                        _logger.LogInformation("📊 CongestionControl [SUSTAINED CONGESTION]: received={Received}, lost={Lost}, " +
                            "bandwidth={Bandwidth:F2}Mbps, lossRate={LossRate:F2}% (triggering passive degradation)",
                            received, lost, bandwidthMbps, lossRate);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in CongestionControl loop");
                    await Task.Delay(100, ct);  // 短暂延迟后继续
                }
            }
            
            _logger.LogInformation("CongestionControl loop exited (sent {Count} packets)", packetCount);
        }
        
        /// <summary>
        /// ✅ 根据网络状况更新自适应上报间隔
        /// </summary>
        private void UpdateAdaptiveInterval()
        {
            var lossRate = _bandwidthEstimator.GetEstimatedLossRate();
            
            // ✅ 根据丢失率调整上报频率
            if (lossRate > HIGH_LOSS_THRESHOLD)
            {
                // 网络状况差：更频繁上报（最小间隔）
                _currentSendIntervalMs = MIN_SEND_INTERVAL_MS;
            }
            else if (lossRate < LOW_LOSS_THRESHOLD)
            {
                // 网络状况好：降低上报频率（最大间隔）
                _currentSendIntervalMs = MAX_SEND_INTERVAL_MS;
            }
            else
            {
                // 网络状况中等：线性插值
                var ratio = (lossRate - LOW_LOSS_THRESHOLD) / (HIGH_LOSS_THRESHOLD - LOW_LOSS_THRESHOLD);
                _currentSendIntervalMs = (int)(BASE_SEND_INTERVAL_MS + 
                    ratio * (MIN_SEND_INTERVAL_MS - BASE_SEND_INTERVAL_MS));
            }
        }
        
        #endregion

        #region Packet Building
        
        /// <summary>
        /// 构造拥塞控制包
        /// 
        /// 格式（15 字节）：
        /// [0x00] Packet Type = 0x05 (CONGESTION)
        /// [0x01-0x02] word_0 = 0x0000 (固定值，Chiaki 中总是 0)
        /// [0x03-0x04] Packets Received (uint16, big-endian)
        /// [0x05-0x06] Packets Lost (uint16, big-endian)
        /// [0x07-0x0a] GMAC (4 bytes, 稍后填充)
        /// [0x0b-0x0e] Key Position (uint32, big-endian)
        /// 
        /// 参考 takion_format_congestion()
        /// ⚠️ 注意：word_0 不是 sequence number，在 Chiaki 中总是初始化为 0
        /// </summary>
        private byte[] BuildCongestionPacket(ushort seqNum, ushort received, ushort lost)
        {
            var buffer = new byte[CONGESTION_PACKET_SIZE];
            int offset = 0;
            
            // [0x00] Packet Type
            buffer[offset++] = 0x05;  // TAKION_PACKET_TYPE_CONGESTION
            
            // [0x01-0x02] word_0 = 0 (固定值，不是 sequence number!)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), 0);
            offset += 2;
            
            // [0x03-0x04] Packets Received (big-endian)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), received);
            offset += 2;
            
            // [0x05-0x06] Packets Lost (big-endian)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), lost);
            offset += 2;
            
            // [0x07-0x0a] GMAC (4 bytes, 稍后由加密层填充)
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), 0);
            offset += 4;
            
            // [0x0b-0x0e] Key Position (big-endian)
            var keyPos = _getKeyPosFunc();
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), (uint)keyPos);
            offset += 4;
            
            return buffer;
        }
        
        #endregion

        /// <summary>
        /// 强制一次高丢失样本（用于快速恢复）
        /// </summary>
        public void ForceHighLossSample(ushort received = 5, ushort lost = 5)
        {
            lock (_statsLock)
            {
                _overrideSample = (received, lost);
            }
        }

        /// <summary>
        /// 启用持续拥塞模式（用于触发被动降档）
        /// 在此模式下，拥塞控制会持续报告高丢失，直到调用 DisableSustainedCongestion()
        /// </summary>
        public void EnableSustainedCongestion(ushort received = 5, ushort lost = 5)
        {
            lock (_statsLock)
            {
                _sustainedCongestionMode = true;
                _sustainedCongestionSample = (received, lost);
            }
        }

        /// <summary>
        /// 禁用持续拥塞模式（流健康恢复后调用）
        /// </summary>
        public void DisableSustainedCongestion()
        {
            lock (_statsLock)
            {
                _sustainedCongestionMode = false;
            }
        }

        /// <summary>
        /// 检查是否处于持续拥塞模式
        /// </summary>
        public bool IsSustainedCongestionEnabled()
        {
            lock (_statsLock)
            {
                return _sustainedCongestionMode;
            }
        }
    }
}

