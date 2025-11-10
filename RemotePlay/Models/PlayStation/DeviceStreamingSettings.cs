using System.Collections.Generic;

namespace RemotePlay.Models.PlayStation
{
    public class DeviceStreamingSettings
    {
        public string? Resolution { get; set; }
        public string? FrameRate { get; set; }
        public string? Bitrate { get; set; }
        public string? Quality { get; set; }
        public string? StreamType { get; set; }
    }

    public class DeviceResolutionOption
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string LabelKey { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Bitrate { get; set; }
    }

    public class DeviceFrameRateOption
    {
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string LabelKey { get; set; } = string.Empty;
        public string? Fps { get; set; }
    }

    public class DeviceBitrateOption
    {
        public string Bitrate { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Quality { get; set; } = string.Empty;
        public string LabelKey { get; set; } = string.Empty;
    }

    public class DeviceStreamTypeOption
    {
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string LabelKey { get; set; } = string.Empty;
    }

    public class DeviceStreamingOptions
    {
        public List<DeviceResolutionOption> Resolutions { get; set; } = new();
        public List<DeviceFrameRateOption> FrameRates { get; set; } = new();
        public List<DeviceBitrateOption> Bitrates { get; set; } = new();
        public List<DeviceStreamTypeOption> StreamTypes { get; set; } = new();
    }

    public class DeviceSettingsResponse
    {
        public DeviceStreamingSettings Settings { get; set; } = new();
        public DeviceStreamingOptions Options { get; set; } = new();
    }
}


