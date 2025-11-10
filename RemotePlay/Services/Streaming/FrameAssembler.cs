using Microsoft.Extensions.Logging;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Utils;
using System.Collections.Concurrent;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// 优化版帧组装器 - 支持超时丢帧、放宽乱序、缓存控制
    /// </summary>
    public sealed class FrameAssembler
    {
        private readonly ILogger<FrameAssembler> _logger;

        private readonly ConcurrentDictionary<int, StreamState> _videoStreams = new();
        private readonly ConcurrentDictionary<int, StreamState> _audioStreams = new();

        private int _currentVideoFrame = -1;
        private int _lastCompleteVideoFrame = -1;
        private int _videoLastUnit = -1;
        private List<int> _videoMissingUnits = new();
        private List<byte[]> _videoPackets = new();
        private DateTime _videoFrameStartTime;

        private int _currentAudioFrame = -1;
        private int _lastCompleteAudioFrame = -1;
        private int _audioLastUnit = -1;
        private List<int> _audioMissingUnits = new();
        private List<byte[]> _audioPackets = new();
        private DateTime _audioFrameStartTime;

        // 丢包统计
        private int _videoLostPackets = 0;
        private int _videoReceivedPackets = 0;
        private int _audioLostPackets = 0;
        private int _audioReceivedPackets = 0;

        // 回调
        public delegate void CorruptFrameHandler(int lastComplete, int current, bool isVideo);
        public event CorruptFrameHandler? OnCorruptFrame;

        // 配置
        private readonly int MaxFrameWaitMs = 50;          // 超时丢帧
        private readonly int MaxOutstandingFrames = 2;    // 最大未完成帧数
        private readonly int MaxPacketsPerFrame = 128;    // 单帧最大包数

        public FrameAssembler(ILogger<FrameAssembler> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 添加视频包
        /// </summary>
        public byte[]? AddVideo(AVPacket packet, byte[] unitData)
        {
            return ProcessPacket(packet, unitData, true);
        }

        /// <summary>
        /// 添加音频包
        /// </summary>
        public byte[]? AddAudio(AVPacket packet, byte[] unitData)
        {
            return ProcessPacket(packet, unitData, false);
        }

        private byte[]? ProcessPacket(AVPacket packet, byte[] unitData, bool isVideo)
        {
            // 超时丢帧
            if (isVideo)
            {
                if (_currentVideoFrame != -1 && 
                    (DateTime.UtcNow - _videoFrameStartTime).TotalMilliseconds > MaxFrameWaitMs)
                {
                    _logger.LogWarning("⚠️ 视频帧 {Frame} 超时丢弃", _currentVideoFrame);
                    ResetFrame(true);
                }
            }
            else
            {
                if (_currentAudioFrame != -1 &&
                    (DateTime.UtcNow - _audioFrameStartTime).TotalMilliseconds > MaxFrameWaitMs)
                {
                    _logger.LogWarning("⚠️ 音频帧 {Frame} 超时丢弃", _currentAudioFrame);
                    ResetFrame(false);
                }
            }

            // 更新计数器
            if (isVideo) { _videoReceivedPackets++; if (_videoReceivedPackets > 65535) _videoReceivedPackets = 1; }
            else { _audioReceivedPackets++; if (_audioReceivedPackets > 65535) _audioReceivedPackets = 1; }

            // 丢弃过期帧
            if (isVideo && packet.FrameIndex <= _lastCompleteVideoFrame) return null;
            if (!isVideo && packet.FrameIndex <= _lastCompleteAudioFrame) return null;

            // 检测新帧
            if (isVideo && packet.FrameIndex != _currentVideoFrame)
            {
                if (_lastCompleteVideoFrame + 1 != packet.FrameIndex)
                    OnCorruptFrame?.Invoke(_lastCompleteVideoFrame + 1, packet.FrameIndex, true);

                SetNewFrame(packet, true);
                _currentVideoFrame = packet.FrameIndex;
            }
            else if (!isVideo && packet.FrameIndex != _currentAudioFrame)
            {
                if (_lastCompleteAudioFrame + 1 != packet.FrameIndex)
                    OnCorruptFrame?.Invoke(_lastCompleteAudioFrame + 1, packet.FrameIndex, false);

                SetNewFrame(packet, false);
                _currentAudioFrame = packet.FrameIndex;
            }

            // 检查包顺序，但不阻塞整帧
            if (isVideo)
            {
                if (packet.UnitIndex != _videoLastUnit + 1)
                {
                    for (int i = _videoLastUnit + 1; i < packet.UnitIndex; i++)
                    {
                        _videoPackets.Add(Array.Empty<byte>());
                        _videoMissingUnits.Add(i);
                        _videoLostPackets++;
                        if (_videoLostPackets > 65535) _videoLostPackets = 1;
                    }
                }

                _videoLastUnit = packet.UnitIndex;
                _videoPackets.Add(unitData.Length > 2 ? unitData[2..] : Array.Empty<byte>());

                if (packet.IsLastSrc || packet.IsLast)
                {
                    if (_videoPackets.Count < packet.UnitsSrc)
                        return null;

                    _lastCompleteVideoFrame = packet.FrameIndex;
                    var frameData = ConcatVideoPackets(_videoPackets, packet.UnitsSrc);
                    ResetFrame(true);
                    return frameData;
                }
            }
            else
            {
                if (packet.UnitIndex != _audioLastUnit + 1)
                {
                    for (int i = _audioLastUnit + 1; i < packet.UnitIndex; i++)
                    {
                        _audioPackets.Add(Array.Empty<byte>());
                        _audioMissingUnits.Add(i);
                        _audioLostPackets++;
                        if (_audioLostPackets > 65535) _audioLostPackets = 1;
                    }
                }

                _audioLastUnit = packet.UnitIndex;
                _audioPackets.Add(unitData);

                if (packet.IsLastSrc || packet.IsLast)
                {
                    if (_audioPackets.Count < packet.UnitsSrc)
                        return null;

                    _lastCompleteAudioFrame = packet.FrameIndex;
                    var frameData = ConcatAudioPackets(_audioPackets, packet.UnitsSrc);
                    ResetFrame(false);
                    return frameData;
                }
            }

            return null;
        }

        private void SetNewFrame(AVPacket packet, bool isVideo)
        {
            if (isVideo)
            {
                _videoFrameStartTime = DateTime.UtcNow;
                _videoPackets.Clear();
                _videoMissingUnits.Clear();
                _videoLastUnit = -1;
            }
            else
            {
                _audioFrameStartTime = DateTime.UtcNow;
                _audioPackets.Clear();
                _audioMissingUnits.Clear();
                _audioLastUnit = -1;
            }
        }

        private void ResetFrame(bool isVideo)
        {
            if (isVideo)
            {
                _videoPackets.Clear();
                _videoMissingUnits.Clear();
                _videoLastUnit = -1;
                _currentVideoFrame = -1;
            }
            else
            {
                _audioPackets.Clear();
                _audioMissingUnits.Clear();
                _audioLastUnit = -1;
                _currentAudioFrame = -1;
            }
        }

        private static byte[] ConcatVideoPackets(List<byte[]> packets, int srcCount)
        {
            int total = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
                total += packets[i].Length;

            var buf = new byte[total];
            int offset = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
            {
                var pkt = packets[i];
                Buffer.BlockCopy(pkt, 0, buf, offset, pkt.Length);
                offset += pkt.Length;
            }
            return buf;
        }

        private static byte[] ConcatAudioPackets(List<byte[]> packets, int srcCount)
        {
            int total = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
                total += packets[i].Length;

            var buf = new byte[total];
            int offset = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
            {
                var pkt = packets[i];
                Buffer.BlockCopy(pkt, 0, buf, offset, pkt.Length);
                offset += pkt.Length;
            }
            return buf;
        }

        public int VideoLostPackets => _videoLostPackets;
        public int VideoReceivedPackets => _videoReceivedPackets;
        public int AudioLostPackets => _audioLostPackets;
        public int AudioReceivedPackets => _audioReceivedPackets;

        private class StreamState
        {
            public int FrameIndex { get; set; }
        }
    }
}
