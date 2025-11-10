namespace RemotePlay.Models.PlayStation
{
    public class RegisterDeviceRequest
    {
        public string HostIp { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
    }
}

