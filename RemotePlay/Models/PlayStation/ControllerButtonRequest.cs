namespace RemotePlay.Models.PlayStation
{
    public class ControllerButtonRequest
    {
        public Guid SessionId { get; set; }
        public string Button { get; set; } = string.Empty;
        public string? Action { get; set; } = "tap";  // press, release, tap
        public int? DelayMs { get; set; } = 100;
    }
}

