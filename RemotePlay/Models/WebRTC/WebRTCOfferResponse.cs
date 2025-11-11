namespace RemotePlay.Models.WebRTC
{
    public class WebRTCOfferResponse
    {
        public required string SessionId { get; set; }
        public required string Sdp { get; set; }
        public required string Type { get; set; }
    }
}

