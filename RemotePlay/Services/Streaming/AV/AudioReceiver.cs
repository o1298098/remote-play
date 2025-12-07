using RemotePlay.Services.Streaming.Protocol;
using System;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 音频接收器：处理音频 AV 包，提取并输出 source unit
    /// </summary>
    public class AudioReceiver
    {
        private readonly ILogger<AudioReceiver>? _logger;

        private ushort _frameIndexPrev = 0;

        private Action<int>? _onFrameLossCallback;

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

            for (int i = 0; i < srcCount; i++)
            {
                ushort frameIndex = (ushort)((packet.FrameIndex + i) & 0xFFFF);
                
                if (!SeqGreater(frameIndex, _frameIndexPrev))
                    continue;
                
                _frameIndexPrev = frameIndex;
                
                int offset = unitSize * i;
                byte[] unitData = new byte[unitSize];
                System.Buffer.BlockCopy(decryptedData, offset, unitData, 0, unitSize);
                
                onFrameReady(unitData);
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
    }
}
