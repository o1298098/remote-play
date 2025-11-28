using System;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 视频帧封装
    /// </summary>
    internal class VideoFrame
    {
        public byte[] Data { get; }
        public bool IsIdr { get; }
        public DateTime Timestamp { get; }
        public uint RtpTimestamp { get; set; }
        public long FrameIndex { get; }

        public VideoFrame(byte[] data, bool isIdr, long frameIndex, DateTime timestamp)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            IsIdr = isIdr;
            FrameIndex = frameIndex;
            Timestamp = timestamp;
        }
    }
}

