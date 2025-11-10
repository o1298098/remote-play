namespace RemotePlay.Models.PlayStation
{
    public class StreamLaunchOptions
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 60;
        public int BitrateKbps { get; set; } = 8000;
        public string VideoCodec { get; set; } = "hevc";
        public bool Hdr { get; set; }
    }
}

