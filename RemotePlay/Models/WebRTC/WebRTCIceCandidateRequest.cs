namespace RemotePlay.Models.WebRTC
{
    public class WebRTCIceCandidateRequest
    {
        public required string SessionId { get; set; }
        public required string Candidate { get; set; }
        public string? SdpMid { get; set; }
        public ushort SdpMLineIndex { get; set; }
    }
}

