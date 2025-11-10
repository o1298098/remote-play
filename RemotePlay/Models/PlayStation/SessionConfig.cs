namespace RemotePlay.Models.PlayStation
{
    public class SessionConfig
    {
        public int ControlPort { get; set; } = 9295; // 注册/控制端口（沿用注册）
        public int VideoPort { get; set; } = 9296;
        public int InputPort { get; set; } = 9297;

        public int ConnectTimeoutMs { get; set; } = 5000;
        public int ReadTimeoutMs { get; set; } = 5000;

        // 兼容 PS4/PS5 的 RP 版本标记
        public string RpVersionPs4 { get; set; } = "10.0";
        public string RpVersionPs5 { get; set; } = "1.0";

        // 默认流参数
        public string DefaultResolution { get; set; } = "720p";
        public string DefaultFps { get; set; } = "30"; // 30fps
        public string DefaultQuality { get; set; } = "default";
    }
}
