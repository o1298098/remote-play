namespace RemotePlay.Models.PlayStation
{
    public class BindDeviceRequest
    {
        public string HostIp { get; set; } = string.Empty;
        public string? AccountId { get; set; }
        public string? Pin { get; set; }
        public string? DeviceName { get; set; }
    }
}

