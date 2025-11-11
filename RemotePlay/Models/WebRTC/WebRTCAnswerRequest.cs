namespace RemotePlay.Models.WebRTC
{
    public class WebRTCAnswerRequest
    {
        public required string SessionId { get; set; }
        public required string Sdp { get; set; }
        public string Type { get; set; } = "answer";
    }
}

