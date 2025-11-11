using System;

namespace RemotePlay.Models.WebRTC
{
    public class WebRTCSessionStatus
    {
        public required string SessionId { get; set; }
        public required string ConnectionState { get; set; }
        public required string IceConnectionState { get; set; }
        public DateTime CreatedAt { get; set; }
        public TimeSpan Age { get; set; }
    }
}

