

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 音频接收器
    /// 每个 unit 作为独立的音频帧处理，而不是组装多个 unit
    /// </summary>
    public class AudioReceiver
    {
        private readonly ILogger<AudioReceiver>? _logger;
        private ushort _frameIndexPrev = 0;
        private bool _frameIndexStartup = true;

        public AudioReceiver(ILogger<AudioReceiver>? logger = null)
        {
            _logger = logger;
        }

        public void SetHeader(byte[] header)
        {
            // Header 通过 stream_info 单独处理，这里不需要保存
        }

        /// <summary>
        /// 处理音频 AV 包
        /// 注意：一个 AV packet 包含多个 unit，每个 unit 作为独立的音频帧
        /// </summary>
        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[]> onFrameReady)
        {
            if (packet.Type != HeaderType.AUDIO)
                return;

            int sourceUnitsCount = packet.UnitsSrc;
            int fecUnitsCount = packet.UnitsFec;
            int unitSize = packet.AudioUnitSize;

            if (decryptedData.Length == 0)
            {
                _logger?.LogError("Audio AV Packet is empty");
                return;
            }

            if (sourceUnitsCount + fecUnitsCount != packet.UnitsTotal)
            {
                _logger?.LogError("Source Units + FEC Units != Total Units in Audio AV Packet: {Src}+{Fec}!={Total}",
                    sourceUnitsCount, fecUnitsCount, packet.UnitsTotal);
                return;
            }

            // 检查数据大小是否匹配
            int expectedDataSize = unitSize * packet.UnitsTotal;
            if (decryptedData.Length != expectedDataSize)
            {
                _logger?.LogError("Audio AV Packet size mismatch: expected {Expected}, got {Actual}",
                    expectedDataSize, decryptedData.Length);
                return;
            }

            // 启动期检测
            if (packet.FrameIndex > (1 << 15))
                _frameIndexStartup = false;

            // 遍历所有 unit（source + fec），每个 unit 作为独立的音频帧
            for (int i = 0; i < sourceUnitsCount + fecUnitsCount; i++)
            {
                ushort frameIndex;
                if (i < sourceUnitsCount)
                {
                    // Source unit: frame_index = packet.frame_index + i
                    frameIndex = (ushort)(packet.FrameIndex + i);
                }
                else
                {
                    // FEC unit
                    int fecIndex = i - sourceUnitsCount;

                    // 启动期：忽略重复的帧
                    if (_frameIndexStartup && packet.FrameIndex + fecIndex < fecUnitsCount + 1)
                        continue;

                    // FEC unit: frame_index = packet.frame_index - fec_units_count + fec_index
                    frameIndex = (ushort)(packet.FrameIndex - fecUnitsCount + fecIndex);
                }

                // 提取当前 unit 的数据
                int unitOffset = unitSize * i;
                if (unitOffset + unitSize > decryptedData.Length)
                {
                    _logger?.LogError("Audio unit {Unit} offset out of bounds: offset={Offset}, size={Size}, dataLen={DataLen}",
                        i, unitOffset, unitSize, decryptedData.Length);
                    continue;
                }

                byte[] unitData = new byte[unitSize];
                Buffer.BlockCopy(decryptedData, unitOffset, unitData, 0, unitSize);

                // 发送单个 unit 作为音频帧
                ProcessSingleUnit(frameIndex, unitData, onFrameReady);
            }
        }

        /// <summary>
        /// 处理单个音频 unit
        /// </summary>
        private void ProcessSingleUnit(ushort frameIndex, byte[] unitData, Action<byte[]> onFrameReady)
        {
            // 检查 frame_index 是否大于上一个
            if (!IsFrameIndexGreater(frameIndex, _frameIndexPrev))
                return; // 跳过旧的或重复的帧

            _frameIndexPrev = frameIndex;

            // 直接发送 unit 数据作为音频帧
            onFrameReady(unitData);
        }

        /// <summary>
        /// 检查 frame_index 是否大于 prev
        /// </summary>
        private static bool IsFrameIndexGreater(ushort frameIndex, ushort prev)
        {
            // 16位序列号比较，考虑回绕
            int diff = (frameIndex - prev) & 0xFFFF;
            return diff > 0 && diff < 0x8000;
        }
    }
}

