using System;

namespace RemotePlay.Models.PlayStation
{
    public class UserDeviceDto
    {
        public string UserDeviceId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string HostId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string? HostType { get; set; }
        public string? IpAddress { get; set; }
        public string? SystemVersion { get; set; }
        public bool IsRegistered { get; set; }
        public string? Status { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DeviceStreamingSettings Settings { get; set; } = new();
    }
}


