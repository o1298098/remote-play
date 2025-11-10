using RemotePlay.Models.PlayStation;

namespace RemotePlay.Services.Streaming.Receiver
{
    public class DefaultReceiver : IAVReceiver
    {
        private readonly ILogger<DefaultReceiver> _logger;
        private int _videoPacketCount = 0;
        private int _audioPacketCount = 0;
        private DateTime _lastLogTime = DateTime.Now;

        public DefaultReceiver(ILogger<DefaultReceiver> logger)
        {
            _logger = logger;
        }

        public void OnAudioPacket(byte[] packet)
        {
            _audioPacketCount++;
            LogStats();
        }

        public void OnVideoPacket(byte[] packet)
        {
            _videoPacketCount++;
            LogStats();
        }

        public void OnStreamInfo(byte[] videoHeader, byte[] audioHeader)
        {
            _logger.LogInformation("📺 StreamInfo received: videoHeader={VH} bytes, audioHeader={AH} bytes", 
                videoHeader?.Length ?? 0, audioHeader?.Length ?? 0);
        }

        public void SetVideoCodec(string codec)
        {
            _logger.LogDebug("📹 视频编码格式: {Codec}", codec);
        }

        public void SetAudioCodec(string codec)
        {
            _logger.LogDebug("🎵 音频编码格式: {Codec}", codec);
        }

        public void EnterWaitForIdr()
        {
            // DefaultReceiver 不需要等待 IDR 帧，仅用于调试
        }

        private void LogStats()
        {
            // 每 5 秒输出一次统计信息
            var now = DateTime.Now;
            if ((now - _lastLogTime).TotalSeconds >= 5)
            {
                _logger.LogInformation("📊 Received packets: Video={VideoCount}, Audio={AudioCount}", 
                    _videoPacketCount, _audioPacketCount);
                _lastLogTime = now;
            }
        }
    }
}


