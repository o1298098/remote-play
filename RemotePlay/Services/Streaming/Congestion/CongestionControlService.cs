using System.Buffers.Binary;
using RemotePlay.Services.Streaming.Congestion;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 拥塞控制服务 - 定期向主机报告网络统计信息
    /// 让主机能够动态调整码率和质量
    /// </summary>
    public class CongestionControlService : IDisposable
    {
        #region Constants
        
        // 上报间隔（固定 200ms）
        private const int CONGESTION_CONTROL_INTERVAL_MS = 200;
        
        // 拥塞控制包大小（15 字节 = 0x0f）
        private const int CONGESTION_PACKET_SIZE = 15;
        
        // 默认最大丢失率（如果超过此值，会限制报告的丢失率）
        private const double DEFAULT_PACKET_LOSS_MAX = 1.0; // 100%（完全移除限制，让PS5看到真实的丢失率）
        
        #endregion

        #region Fields
        
        private readonly ILogger<CongestionControlService> _logger;
        private readonly Func<byte[], Task> _sendRawFunc;  // 发送原始包的回调
        private readonly Func<ulong> _getKeyPosFunc;       // 获取 key_pos 的回调
        private readonly Func<(ushort, ushort)>? _getPacketStatsFunc;  // 获取包统计的回调（可选，应该返回增量值并重置）
        
        private CancellationTokenSource? _cts;
        private Task? _congestionLoop;
        
        private double _packetLossMax = DEFAULT_PACKET_LOSS_MAX; // 最大丢失率（超过此值会限制报告的丢失率）
        private double _packetLoss = 0; // 当前丢失率
        
        private bool _isRunning = false;
        
        #endregion

        #region Constructor & Lifecycle
        
        /// <summary>
        /// 创建拥塞控制服务
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="sendRawFunc">发送原始包的回调函数</param>
        /// <param name="getKeyPosFunc">获取当前 key_pos 的回调函数</param>
        /// <param name="getPacketStatsFunc">获取包统计的回调函数（可选，应该返回增量值并重置统计）</param>
        /// <param name="packetLossMax">最大丢失率（超过此值会限制报告的丢失率，默认 1.0）</param>
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
            
            _logger.LogDebug("CongestionControl started (interval={IntervalMs}ms, packet_loss_max={LossMax:P2})", 
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
            // 使用超时机制，避免 Dispose 阻塞太久
            try
            {
                var stopTask = StopAsync();
                var timeoutTask = Task.Delay(1000); // 最多等待 1 秒
                var completedTask = Task.WhenAny(stopTask, timeoutTask).GetAwaiter().GetResult();
                
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("CongestionControl StopAsync 超时（1秒），强制继续释放");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CongestionControl Dispose 异常，继续释放");
            }
            
            _cts?.Dispose();
        }
        
        #endregion

        #region Congestion Loop
        
        /// <summary>
        /// 拥塞控制主循环
        /// 每 200ms 发送一次拥塞控制包，报告网络统计信息
        /// </summary>
        private async Task CongestionLoopAsync(CancellationToken ct)
        {
            _logger.LogDebug("CongestionControl loop started");
            
            int packetCount = 0;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 固定间隔（200ms）
                    await Task.Delay(CONGESTION_CONTROL_INTERVAL_MS, ct);
                    
                    // 获取包统计信息（应该返回增量值并重置）
                    ulong received;
                    ulong lost;
                    
                    if (_getPacketStatsFunc != null)
                    {
                        var (received16, lost16) = _getPacketStatsFunc();
                        received = received16;
                        lost = lost16;
                    }
                    else
                    {
                        // 如果没有回调函数，使用默认值
                        received = 0;
                        lost = 0;
                    }
                    
                    // 计算丢失率
                    ulong total = received + lost;
                    _packetLoss = total > 0 ? (double)lost / total : 0;
                    
                    // 如果丢失率超过最大值，限制报告的丢失率
                    if (_packetLoss > _packetLossMax)
                    {
                        _logger.LogWarning("Increasing received packets to reduce hit on stream quality");
                        lost = (ulong)(total * _packetLossMax);
                        received = total - lost;
                    }
                    
                    // 构造并发送拥塞包
                    var packet = BuildCongestionPacket((ushort)received, (ushort)lost);
                    await _sendRawFunc(packet);
                    
                    packetCount++;
                    
                    // 详细日志（仅在 Verbose 级别）
                    _logger.LogTrace("Sending Congestion Control Packet, received: {Received}, lost: {Lost}",
                        received, lost);
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
        /// 格式（15 字节 = 0x0f）：
        /// [0x00] Packet Type = 0x05 (TAKION_PACKET_TYPE_CONGESTION)
        /// [0x01-0x02] word_0 = 0x0000 (固定值，总是 0)
        /// [0x03-0x04] Packets Received (uint16, big-endian)
        /// [0x05-0x06] Packets Lost (uint16, big-endian)
        /// [0x07-0x0a] GMAC (4 bytes, 稍后由加密层填充)
        /// [0x0b-0x0e] Key Position (uint32, big-endian)
        /// </summary>
        private byte[] BuildCongestionPacket(ushort received, ushort lost)
        {
            var buffer = new byte[CONGESTION_PACKET_SIZE];
            int offset = 0;
            
            // [0x00] Packet Type
            buffer[offset++] = 0x05;  // TAKION_PACKET_TYPE_CONGESTION
            
            // [0x01-0x02] word_0 = 0 (固定值，总是 0)
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
            // 注意：key_pos 会在发送时由 SendCongestionControlPacket 更新
            // 这里先写入 0 作为占位符，发送时会先推进 key_pos 然后更新
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), 0);
            offset += 4;
            
            return buffer;
        }
        
        #endregion

    }
}

