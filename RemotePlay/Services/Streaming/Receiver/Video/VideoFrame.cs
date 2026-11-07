using System;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 视频帧封装
    /// </summary>
    internal sealed class VideoFrame
    {
        public byte[] Data { get; }
        public bool IsIdr { get; }
        public long FrameIndex { get; }

        /// <summary>
        /// 到达时间（Stopwatch ticks），用于计算 RTP 帧间隔
        /// </summary>
        public long ArrivalTicks { get; }

        public VideoFrame(byte[] data, bool isIdr, long frameIndex, long arrivalTicks)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            IsIdr = isIdr;
            FrameIndex = frameIndex;
            ArrivalTicks = arrivalTicks;
        }
    }
}
