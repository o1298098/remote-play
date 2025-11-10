using System.Threading.Tasks;

namespace RemotePlay.Models.PlayStation
{
    public class RemoteSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string HostIp { get; set; } = string.Empty;
        public string HostType { get; set; } = "PS4"; // PS4 | PS5
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public byte[] SessionId { get; set; } = Array.Empty<byte>();

        public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? StoppedAtUtc { get; set; }

        public bool IsActive => StoppedAtUtc == null;

        // 握手/流加密相关
        public byte[] HandshakeKey { get; set; } = Array.Empty<byte>();
        public byte[] Secret { get; set; } = Array.Empty<byte>();
        // Session AES-CFB IV (rp_iv / nonce)
        public byte[] SessionIv { get; set; } = Array.Empty<byte>();

        // 会话层计数器（与 SessionCipher 对齐）
        public uint EncCounter { get; set; }
        public uint DecCounter { get; set; }

        // 可选：保存当前视频/输入通道状态
        public int VideoKeyPos { get; set; }
        public int InputKeyPos { get; set; }

        // 流参数
        public string Resolution { get; set; } = "720p";
        public string Fps { get; set; } = "30";
        public string Quality { get; set; } = "default";
        public string? Bitrate { get; set; }
        public string? StreamType { get; set; }

        public StreamLaunchOptions LaunchOptions { get; set; } = new StreamLaunchOptions();
        
        // Senkusha 测试结果（网络参数）
        // ⚠️ 默认值基于 Chiaki 的实际测试结果（10.952ms RTT）
        public long RttUs { get; set; } = 10952;  // RTT in microseconds, Chiaki 实测值
        public int MtuIn { get; set; } = 1454;    // MTU IN, default 1454
        public int MtuOut { get; set; } = 1454;   // MTU OUT, default 1454

        // 会话就绪信号：收到并存储 SessionId 后置为完成
        public TaskCompletionSource<bool> SessionReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
