namespace RemotePlay.Models.Configuration
{
    public class RemotePlayConfig
    {
        public DiscoveryConfig Discovery { get; set; } = new();
        public RegistrationConfig Registration { get; set; } = new();
        public SecurityConfig Security { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public StreamingConfig Streaming { get; set; } = new();
    }

    public class StreamingConfig
    {
        /// <summary>
        /// 上报给主机的丢包率上限（0~1）。主机据此调整码率和分辨率，值越大降档越激进
        /// </summary>
        public double PacketLossReportedMax { get; set; } = 0.05;
    }

    public class DiscoveryConfig
    {
        public int TimeoutMs { get; set; } = 2000;
        public int DiscoveryPort { get; set; } = 9302;
        public int ClientPort { get; set; } = 9303;
        public string ProtocolVersion { get; set; } = "00030010";
        public int MaxRetries { get; set; } = 3;
    }

    public class RegistrationConfig
    {
        public int TimeoutMs { get; set; } = 30000;
        public string Endpoint { get; set; } = "/sce/rp/rp/session";
        public int MaxRetries { get; set; } = 3;
        public int CredentialExpiryDays { get; set; } = 30;
    }

    public class SecurityConfig
    {
        public int KeyLength { get; set; } = 32;
        public int PinLength { get; set; } = 8;
        public bool EnableEncryption { get; set; } = true;
        public string EncryptionAlgorithm { get; set; } = "AES-256-CBC";
    }

    public class LoggingConfig
    {
        public bool EnableDebugLogging { get; set; } = false;
        public bool LogNetworkTraffic { get; set; } = false;
        public string LogLevel { get; set; } = "Information";
    }
}
