namespace RemotePlay.Services.Streaming.Quality
{
    /// <summary>
    /// 每个 Profile 包含一个分辨率及其对应的 header
    /// </summary>
    public class VideoProfile
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] Header { get; set; } = Array.Empty<byte>();
        public byte[] HeaderWithPadding { get; set; } = Array.Empty<byte>();

        public VideoProfile(int index, int width, int height, byte[] header)
        {
            Index = index;
            Width = width;
            Height = height;
            Header = header;
            
            // 添加 64 字节 padding（FFMPEG 要求）
            if (header.Length > 0)
            {
                var padding = new byte[64];
                HeaderWithPadding = new byte[header.Length + padding.Length];
                System.Buffer.BlockCopy(header, 0, HeaderWithPadding, 0, header.Length);
                System.Buffer.BlockCopy(padding, 0, HeaderWithPadding, header.Length, padding.Length);
            }
        }

        public override string ToString() => $"Profile[{Index}]: {Width}x{Height}";
    }
}

