namespace RemotePlay.Models.PlayStation
{
    public class RegisterResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DeviceCredentials? Credentials { get; set; }
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string HostType { get; set; } = string.Empty;
        public string SystemVersion { get; set; } = string.Empty;
        public string DeviceDiscoverPotocolVersion { get; set; } = string.Empty;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public TimeSpan Duration { get; set; }

        public Dictionary<string, string>? RegistData;
    }
}
