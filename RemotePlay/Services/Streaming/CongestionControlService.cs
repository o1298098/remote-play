using System.Buffers.Binary;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 拥塞控制服务 - 定期向主机报告网络统计信息
    /// 让主机能够动态调整码率和质量
    /// </summary>
    public class CongestionControlService : IDisposable
    {
        #region Constants
        
        // ✅ 每 66ms 发送一次（约 15Hz）
        private const int CONGESTION_SEND_INTERVAL_MS = 66;
        private const int CONGESTION_PACKET_SIZE = 15;  // 0x0f bytes
        
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
        private (ushort received, ushort lost)? _overrideSample;
        
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
            
            _logger.LogInformation("✅ CongestionControl started - will send every {IntervalMs}ms", 
                CONGESTION_SEND_INTERVAL_MS);
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
        /// ✅ 每 66ms 发送一次拥塞包
        /// </summary>
        private async Task CongestionLoopAsync(CancellationToken ct)
        {
            _logger.LogInformation("🔄 CongestionControl loop started");
            
            int packetCount = 0;
            var startTime = DateTime.UtcNow;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ✅ 等待 66ms
                    await Task.Delay(CONGESTION_SEND_INTERVAL_MS, ct);
                    
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

                        if (_overrideSample.HasValue)
                        {
                            received = _overrideSample.Value.received;
                            lost = _overrideSample.Value.lost;
                            _overrideSample = null;
                        }
                    }
                    
                    // 构造并发送拥塞包
                    var packet = BuildCongestionPacket(seqNum, received, lost);
                    await _sendRawFunc(packet);
                    
                    packetCount++;
                    
                    // ✅ 前5个包记录详细统计
                    if (packetCount <= 5)
                    {
                        _logger.LogInformation("📊 Congestion #{Num}: received={Received}, lost={Lost}, seqNum={Seq}",
                            packetCount, received, lost, seqNum);
                    }
                    // 定期日志（每 30 秒 ~450 包）
                    else if (packetCount % 450 == 0)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        var rate = packetCount / elapsed;
                        
                        _logger.LogInformation("📊 CongestionControl: sent {Count} packets ({Rate:F1}/s), " +
                            "stats: received={Received}, lost={Lost}",
                            packetCount, rate, received, lost);
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

        public void ForceHighLossSample(ushort received = 5, ushort lost = 5)
        {
            lock (_statsLock)
            {
                _overrideSample = (received, lost);
            }
        }
    }
}

