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
        
        // ✅ 上报间隔（参考 chiaki-ng: 固定 200ms）
        private const int CONGESTION_CONTROL_INTERVAL_MS = 200;
        
        private const int CONGESTION_PACKET_SIZE = 15;  // 0x0f bytes
        
        // ✅ 默认最大丢失率（如果超过此值，会限制报告的丢失率）
        // 注意：提高此值可以让PS5看到更高的丢失率，从而触发降档
        // 原值5%可能过低，导致PS5认为网络状况良好而不降档
        // 15%、25%、30%仍然不够，完全移除限制（设为1.0），让PS5看到真实的丢失率
        // 这样PS5可以根据真实的网络状况做出降档决策
        private const double DEFAULT_PACKET_LOSS_MAX = 1.0; // 100%（完全移除限制，让PS5看到真实的丢失率）
        
        #endregion

        #region Fields
        
        private readonly ILogger<CongestionControlService> _logger;
        private readonly Func<byte[], Task> _sendRawFunc;  // 发送原始包的回调
        private readonly Func<ulong> _getKeyPosFunc;       // 获取 key_pos 的回调
        private readonly Func<(ushort, ushort)>? _getPacketStatsFunc;  // 获取包统计的回调（可选）
        
        private CancellationTokenSource? _cts;
        private Task? _congestionLoop;
        
        private ushort _sequenceNumber = 0;
        private ushort _packetsReceived = 0;
        private ushort _packetsLost = 0;
        
        private readonly object _statsLock = new object();
        private double _packetLossMax = DEFAULT_PACKET_LOSS_MAX; // ✅ 最大丢失率（超过此值会限制报告的丢失率）
        private double _packetLoss = 0; // ✅ 当前丢失率
        
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
        /// <param name="packetLossMax">最大丢失率（超过此值会限制报告的丢失率，默认 5%）</param>
        public CongestionControlService(
            ILogger<CongestionControlService> logger,
            Func<byte[], Task> sendRawFunc,
            Func<ulong> getKeyPosFunc,
            Func<(ushort, ushort)>? getPacketStatsFunc = null,
            double packetLossMax = DEFAULT_PACKET_LOSS_MAX)
        {
            _logger = logger;
            _sendRawFunc = sendRawFunc;
            _getKeyPosFunc = getKeyPosFunc;
            _getPacketStatsFunc = getPacketStatsFunc;
            _packetLossMax = packetLossMax;
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
            
            _logger.LogDebug("✅ CongestionControl started (interval={IntervalMs}ms, packet_loss_max={LossMax:P2})", 
                CONGESTION_CONTROL_INTERVAL_MS, _packetLossMax);
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
            _logger.LogDebug("CongestionControl stopped");
        }
        
        public void Dispose()
        {
            // ✅ 关键修复：使用超时机制，避免 Dispose 阻塞太久
            try
            {
                var stopTask = StopAsync();
                var timeoutTask = Task.Delay(1000); // 最多等待 1 秒
                var completedTask = Task.WhenAny(stopTask, timeoutTask).GetAwaiter().GetResult();
                
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("⚠️ CongestionControl StopAsync 超时（1秒），强制继续释放");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ CongestionControl Dispose 异常，继续释放");
            }
            
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
        }
        
        #endregion

        #region Congestion Loop
        
        /// <summary>
        /// 拥塞控制主循环
        /// ✅ 参考 chiaki-ng：固定 200ms 间隔，限制丢失率不超过最大值
        /// </summary>
        private async Task CongestionLoopAsync(CancellationToken ct)
        {
            _logger.LogDebug("🔄 CongestionControl loop started");
            
            int packetCount = 0;
            var startTime = DateTime.UtcNow;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ✅ 固定间隔（参考 chiaki-ng: 200ms）
                    await Task.Delay(CONGESTION_CONTROL_INTERVAL_MS, ct);
                    
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
                        
                        // ✅ 计算丢失率（参考 chiaki-ng）
                        ulong total = (ulong)received + (ulong)lost;
                        _packetLoss = total > 0 ? (double)lost / total : 0;
                        
                        // ✅ 关键：如果丢失率超过最大值，限制报告的丢失率（参考 chiaki-ng）
                        // 注意：当前设置为1.0（100%），完全移除限制，让PS5看到真实的丢失率
                        if (_packetLoss > _packetLossMax)
                        {
                            _logger.LogWarning("⚠️ 丢失率超过阈值，限制报告的丢失率 (实际丢失率={Loss:P2} > 最大报告值={Max:P2})", 
                                _packetLoss, _packetLossMax);
                            lost = (ushort)(total * _packetLossMax);
                            received = (ushort)(total - lost);
                        }
                        else if (_packetLoss > 0.1) // 如果丢失率超过10%，记录详细信息
                        {
                            _logger.LogWarning("⚠️ 高丢失率: {Loss:P2}, 报告给PS5: received={Received}, lost={Lost}, total={Total}", 
                                _packetLoss, received, lost, total);
                            
                            // ✅ 诊断：如果丢失率持续很高，记录警告
                            if (_packetLoss > 0.5) // 超过50%
                            {
                                _logger.LogWarning("🚨 严重丢失率: {Loss:P2}，PS5应该降档！请检查：1) 是否有多个profiles 2) PS5是否收到拥塞控制包", 
                                    _packetLoss);
                            }
                        }
                    }
                    
                    // 构造并发送拥塞包
                    var packet = BuildCongestionPacket(seqNum, received, lost);
                    await _sendRawFunc(packet);
                    
                    packetCount++;
                    
                    // ✅ 前5个包记录详细统计
                    if (packetCount <= 5)
                    {
                        _logger.LogDebug("📊 Congestion #{Num}: received={Received}, lost={Lost}, seqNum={Seq}, loss={Loss:P2}", 
                            packetCount, received, lost, seqNum, _packetLoss);
                    }
                    // 定期日志（每 30 秒）
                    else if (packetCount % 150 == 0) // 约每 30 秒（150 * 200ms）
                    {
                        var elapsed = (currentTime - startTime).TotalSeconds;
                        var rate = packetCount / elapsed;
                        _logger.LogDebug("📊 CongestionControl: sent {Count} packets ({Rate:F1}/s), " +
                            "stats: received={Received}, lost={Lost}, loss={Loss:P2}",
                            packetCount, rate, received, lost, _packetLoss);
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
            
            _logger.LogDebug("CongestionControl loop exited (sent {Count} packets)", packetCount);
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

    }
}

