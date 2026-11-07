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

        /// <summary>
        /// 在 LaunchSpec 中声明多个候选分辨率，允许主机在网络变差时自动降低分辨率。
        /// 当前 LaunchSpec 格式下多一个分辨率就会让 BIG 包超过 MTU（会被自动裁剪回 1 个），因此默认关闭。
        /// </summary>
        public bool AdaptiveResolution { get; set; } = false;
    }
}
