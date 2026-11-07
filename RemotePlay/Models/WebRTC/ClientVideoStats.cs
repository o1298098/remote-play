namespace RemotePlay.Models.WebRTC
{
    /// <summary>
    /// 浏览器端 RTCPeerConnection.getStats() 中视频 inbound-rtp 的摘要，用于诊断
    /// </summary>
    public class ClientVideoStats
    {
        public double? PacketsReceived { get; set; }
        public double? PacketsLost { get; set; }
        public double? FramesReceived { get; set; }
        public double? FramesDecoded { get; set; }
        public double? KeyFramesDecoded { get; set; }
        public double? FramesDropped { get; set; }
        public double? PliCount { get; set; }
        public double? FirCount { get; set; }
        public double? NackCount { get; set; }
        public double? FreezeCount { get; set; }
        public double? TotalFreezesDuration { get; set; }
        public double? DecodeFps { get; set; }
        public double? BitrateKbps { get; set; }
        public double? JitterBufferDelayMs { get; set; }
        public double? FrameWidth { get; set; }
        public double? FrameHeight { get; set; }
        public string? DecoderImplementation { get; set; }
        public string? Codec { get; set; }
        public string? ConnectionState { get; set; }
    }
}
