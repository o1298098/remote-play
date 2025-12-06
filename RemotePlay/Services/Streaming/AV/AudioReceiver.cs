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

            bool isStartup = _frameReceivedCount < STARTUP_FRAME_COUNT;

            for (int i = 0; i < total; i++)
            {
                ushort frameIndex;

                if (i < srcCount)
                {
                    frameIndex = (ushort)((packet.FrameIndex + i) & 0xFFFF);
                }
                else
                {
                    int fecIndex = i - srcCount;

                    if (isStartup)
                        continue; // 启动期忽略所有 FEC

                    frameIndex = (ushort)((packet.FrameIndex - fecCount + fecIndex) & 0xFFFF);
                }

                int offset = unitSize * i;
                byte[] unitData = new byte[unitSize];
                System.Buffer.BlockCopy(decryptedData, offset, unitData, 0, unitSize);

                ProcessSingleUnit(frameIndex, unitData, onFrameReady);
            }
        }

        /// <summary>
        /// RFC1982 标准序列号比较（适用于 16-bit wrap-around）
        /// </summary>
        private static bool SeqGreater(ushort a, ushort b)
        {
            return ((a > b) && (a - b < 0x8000)) ||
                   ((a < b) && (b - a > 0x8000));
        }

        /// <summary>
        /// RFC1982 标准 gap 计算（不把回绕算成巨大 gap）
        /// </summary>
        private static int SeqGap(ushort newer, ushort older)
        {
            return (newer - older) & 0xFFFF;
        }

        private void ProcessSingleUnit(ushort idx, byte[] data, Action<byte[]> onFrameReady)
        {
            if (!SeqGreater(idx, _frameIndexPrev))
                return;

            int gap = SeqGap(idx, _frameIndexPrev);

            if (gap > MAX_FRAME_GAP && gap < 0x8000)
            {
                _logger?.LogWarning("Audio loss: {0} → {1}, lost {2}", _frameIndexPrev, idx, gap);
                _onFrameLossCallback?.Invoke(gap);
            }
            else if (gap > 0x8000)
            {
                _logger?.LogInformation("Audio index wrap: {0} → {1}", _frameIndexPrev, idx);
            }

            _frameIndexPrev = idx;
            _frameReceivedCount++;

            onFrameReady(data);
        }
    }
}
