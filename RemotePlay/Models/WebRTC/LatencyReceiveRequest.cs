using System;

namespace RemotePlay.Models.WebRTC
{
    public class LatencyReceiveRequest
    {
        public required string SessionId { get; set; }
        public required string PacketType { get; set; }
        public long FrameIndex { get; set; }
        public DateTime ClientReceiveTime { get; set; }
    }
}

