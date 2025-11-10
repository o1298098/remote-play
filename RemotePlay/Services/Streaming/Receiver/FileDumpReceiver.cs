using RemotePlay.Models.PlayStation;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed class FileDumpReceiver : IAVReceiver
    {
        private readonly FileStream _videoStream;
        private readonly FileStream _audioStream;

        public FileDumpReceiver(string? videoFilePath = null, string? audioFilePath = null)
        {
            var baseDir = Directory.GetCurrentDirectory();
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var videoPath = string.IsNullOrWhiteSpace(videoFilePath)
                ? Path.Combine(baseDir, $"video_{ts}.h264")
                : Path.GetFullPath(videoFilePath);
            var audioPath = string.IsNullOrWhiteSpace(audioFilePath)
                ? Path.Combine(baseDir, $"audio_{ts}.aac")
                : Path.GetFullPath(audioFilePath);

            _videoStream = new FileStream(videoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _audioStream = new FileStream(audioPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        public void OnStreamInfo(byte[] videoHeader, byte[] audioHeader)
        {
            if (videoHeader != null && videoHeader.Length > 0)
            {
                _videoStream.Write(videoHeader, 0, videoHeader.Length);
                _videoStream.Flush();
            }
            if (audioHeader != null && audioHeader.Length > 0)
            {
                _audioStream.Write(audioHeader, 0, audioHeader.Length);
                _audioStream.Flush();
            }
        }

        public void OnVideoPacket(byte[] packet)
        {
            if (packet == null || packet.Length <= 1) return;
            // 丢弃首字节类型标记（VIDEO=0x02）
            _videoStream.Write(packet, 1, packet.Length - 1);
        }

        public void OnAudioPacket(byte[] packet)
        {
            if (packet == null || packet.Length <= 1) return;
            // 丢弃首字节类型标记（AUDIO=0x01）
            _audioStream.Write(packet, 1, packet.Length - 1);
        }

        public void SetVideoCodec(string codec)
        {
            // 记录但不处理
        }

        public void SetAudioCodec(string codec)
        {
            // 记录但不处理
        }

        public void EnterWaitForIdr()
        {
            // FileDumpReceiver 不需要等待 IDR 帧，直接写入所有数据
        }
    }
}


