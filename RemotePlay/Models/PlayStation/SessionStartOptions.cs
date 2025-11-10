namespace RemotePlay.Models.PlayStation
{
    public class SessionStartOptions
    {
        public string? Resolution { get; set; }
        public string? Fps { get; set; }
        public string? Quality { get; set; }
        public string? Bitrate { get; set; }
        public string? StreamType { get; set; }
        public bool AutoStartStream { get; set; } = true;
        public bool AutoConnectController { get; set; } = true;
        public bool WakeupIfStandby { get; set; } = true;
    }
}
