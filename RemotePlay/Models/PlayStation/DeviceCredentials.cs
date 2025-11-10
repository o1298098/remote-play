namespace RemotePlay.Models.PlayStation
{
    public class DeviceCredentials
    {
        public string AccountId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string HostIp { get; set; } = string.Empty;
        public byte[] RegistrationKey { get; set; } = Array.Empty<byte>();
        public byte[] ServerKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }
        public bool IsValid => DateTime.UtcNow < ExpiresAt;
    }
}
