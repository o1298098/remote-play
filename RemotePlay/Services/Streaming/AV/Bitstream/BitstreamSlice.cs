namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// Slice 类型
    /// </summary>
    public enum SliceType
    {
        Unknown = 0,
        I,  // I-frame
        P   // P-frame
    }

    /// <summary>
    /// Bitstream Slice 信息
    /// </summary>
    public class BitstreamSlice
    {
        public SliceType SliceType { get; set; }
        public uint ReferenceFrame { get; set; } = 0xFF; // 0xFF 表示未找到或无效
    }
}

