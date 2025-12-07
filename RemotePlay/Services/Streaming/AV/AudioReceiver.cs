using RemotePlay.Services.Streaming.Protocol;
using System;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 稳定版音频接收器（带完美的序列号回绕处理）
    /// 每个 unit 独立输出，不组装
    /// </summary>
    public class AudioReceiver
    {
        private readonly ILogger<AudioReceiver>? _logger;

        private ushort _frameIndexPrev = 0;
        private int _frameReceivedCount = 0;

        private Action<int>? _onFrameLossCallback;

        private const int MAX_FRAME_GAP = 5;
        private const int STARTUP_FRAME_COUNT = 10;

        public AudioReceiver(ILogger<AudioReceiver>? logger = null)
        {
            _logger = logger;
        }

        public void SetFrameLossCallback(Action<int>? callback)
        {
            _onFrameLossCallback = callback;
        }

        public void SetHeader(byte[] header)
        {
            // header 不需要在这里处理
        }

        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[]> onFrameReady)
        {
            if (packet.Type != HeaderType.AUDIO)
                return;

            int srcCount = packet.UnitsSrc;
            int fecCount = packet.UnitsFec;
            int total = packet.UnitsTotal;
            int unitSize = packet.AudioUnitSize;

            if (srcCount + fecCount != total)
            {
                _logger?.LogError("Audio Packet Units mismatch {0}+{1}!={2}", srcCount, fecCount, total);
                return;
            }

            int expectedSize = total * unitSize;
            if (expectedSize != decryptedData.Length)
            {
                _logger?.LogError("Audio data size mismatch expected {0} got {1}", expectedSize, decryptedData.Length);
                return;
            }

            // ✅ 关键修复：使用 RFC1982 标准处理 16 位序列号回绕
            // 由于音频数据不经过 ReorderQueue，需要在这里做基本的去重和帧丢失检测
            // 但只检测真正明显的情况，避免误判导致频繁重置解码器
            ushort packetIndex = packet.FrameIndex;
            
            if (_frameReceivedCount > 0)
            {
                // 使用 RFC1982 标准计算序列号差值（考虑 16 位回绕）
                int gap = (packetIndex - _frameIndexPrev) & 0xFFFF;
                
                // 如果 gap > 0x8000，说明是回绕（packetIndex 实际上在 _frameIndexPrev 之前）
                // 这种情况下，packetIndex 是旧的包，应该跳过
                if (gap > 0x8000)
                {
                    // 回绕或旧包，跳过
                    return;
                }
                
                // 只检测真正明显的帧丢失（gap > 200），避免误判
                // 使用较大的阈值，因为 RemotePlay 的序列号不是连续递增的
                // 但 gap 必须 < 0x8000（不是回绕）
                if (gap > 200 && gap < 0x8000)
                {
                    // 真正的帧丢失，触发回调重置解码器
                    _logger?.LogWarning("⚠️ 检测到音频 packet 丢失：从 {Prev} 跳到 {Current}，丢失 {Gap} 帧，将重置解码器",
                        _frameIndexPrev, packetIndex, gap);
                    _onFrameLossCallback?.Invoke(gap);
                }
            }
            
            _frameIndexPrev = packetIndex;
            _frameReceivedCount++;

            // 只输出 source unit（0 到 srcCount-1），不输出 FEC unit
            for (int i = 0; i < srcCount; i++)
            {
                int offset = unitSize * i;
                byte[] unitData = new byte[unitSize];
                System.Buffer.BlockCopy(decryptedData, offset, unitData, 0, unitSize);

                // 直接输出 source unit
                onFrameReady(unitData);
            }
        }
    }
}
