namespace RemotePlay.Models.PlayStation
{
    public class UpdateDeviceSettingsRequest
    {
        public string? Resolution { get; set; }
        public string? FrameRate { get; set; }
        public string? Bitrate { get; set; }
        public string? Quality { get; set; }
        public string? StreamType { get; set; }
    }
}


