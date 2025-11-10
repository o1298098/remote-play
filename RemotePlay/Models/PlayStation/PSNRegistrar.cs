namespace RemotePlay.Models.PlayStation
{

    public enum ChiakiTarget
    {
        PS4_8 = 0x800,
        PS4_9 = 0x900,
        PS4_10 = 0xa00,
        PS4_11 = 0xb00,
        PS4_12 = 0xc00,
        PS5_UNKNOWN = 0x1000,
        PS5_1 = 0x1001
    }

    public enum ChiakiErrorCode
    {
        Success = 0,
        Canceled = 1,
        Timeout = 2,
        Network = 3,
        InvalidData = 4,
        BufTooSmall = 5,
        InvalidResponse = 6,
        Unknown = 7
    }


    public enum ChiakiRegistEventType
    {
        FinishedSuccess,
        FinishedFailed,
        FinishedCanceled
    }

    public class ChiakiRegistInfo
    {
        public string? Host { get; set; }
        public ChiakiTarget Target { get; set; }
        public uint Pin { get; set; }
        public string? PsnOnlineId { get; set; }
        public byte[]? PsnAccountId { get; set; }
        public bool Broadcast { get; set; }
        public uint ConsolePin { get; set; }
        public ChiakiHolepunchRegistInfo? HolepunchInfo { get; set; }
    }

    public class ChiakiHolepunchRegistInfo
    {
        public byte[]? RegistLocalIp { get; set; }
        public byte[]? CustomData1 { get; set; }
        public byte[]? Data1 { get; set; }
        public byte[]? Data2 { get; set; }
    }

    public class ChiakiRegisteredHost
    {
        public ChiakiTarget Target { get; set; }
        public string? ApSsid { get; set; }
        public string? ApBssid { get; set; }
        public string? ApKey { get; set; }
        public string? ApName { get; set; }
        public string? ServerNickname { get; set; }
        public byte[]? RpRegistKey { get; set; }
        public uint RpKeyType { get; set; }
        public byte[]? RpKey { get; set; }
        public byte[]? ServerMac { get; set; }
        public uint ConsolePin { get; set; }
    }

    public class ChiakiRegistEvent
    {
        public ChiakiRegistEventType Type { get; set; }
        public ChiakiRegisteredHost? RegisteredHost { get; set; }
    }

    public delegate void ChiakiRegistCb(ChiakiRegistEvent evt, object user);
}
