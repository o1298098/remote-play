namespace RemotePlay.Models.PlayStation
{
    public interface IAVReceiver
    {
        void OnVideoPacket(byte[] packet);
        void OnAudioPacket(byte[] packet);
        void OnStreamInfo(byte[] videoHeader, byte[] audioHeader);
        /// <summary>
        /// 设置视频编码格式（在检测到第一个视频包时调用）
        /// </summary>
        void SetVideoCodec(string codec); // "h264" 或 "hevc"
        /// <summary>
        /// 设置音频编码格式（在检测到第一个音频包时调用）
        /// </summary>
        void SetAudioCodec(string codec); // "opus" 或 "aac"
        /// <summary>
        /// 重新进入等待 IDR 关键帧模式（在切换 receiver 时调用）
        /// </summary>
        void EnterWaitForIdr();
    }
}


