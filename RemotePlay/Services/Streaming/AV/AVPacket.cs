using System.Buffers.Binary;

namespace RemotePlay.Services.Streaming.AV
{
    public sealed class AVPacket
    {
        public HeaderType Type { get; private set; }
        public bool HasNalu { get; private set; }
        public ushort Index { get; private set; }
        public ushort FrameIndex { get; private set; }
        public uint Dword2 { get; private set; }
        public byte Codec { get; private set; }
        public uint KeyPos { get; private set; }
        public int AdaptiveStreamIndex { get; private set; } = -1;
        public byte[] Data { get; private set; } = Array.Empty<byte>();

        public int UnitIndex { get; private set; }
        public int UnitsTotal { get; private set; }
        public int UnitsSrc { get; private set; }
        public int UnitsFec { get; private set; }
        public int AudioUnitSize { get; private set; }

        public bool IsLast => UnitIndex == UnitsTotal - 1;
        public bool IsLastSrc => UnitIndex == UnitsSrc - 1;
        
        /// <summary>
        /// 是否为 FEC 包（unit_index >= frame_length_src）
        /// </summary>
        public bool IsFec => UnitIndex >= UnitsSrc;

        public static bool TryParse(byte[] buf, string hostType, out AVPacket packet)
        {
            packet = new AVPacket();
            if (buf == null || buf.Length < 18) return false;
            var t = (HeaderType)(buf[0] & 0x0F);
            if (t != HeaderType.VIDEO && t != HeaderType.AUDIO) return false;
            packet.Type = t;

            packet.HasNalu = ((buf[0] >> 4) & 1) != 0;
            packet.Index = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(1, 2));
            packet.FrameIndex = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(3, 2));
            packet.Dword2 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(5, 4));
            packet.Codec = buf[9];
            // unknown at 10 (skip)
            packet.KeyPos = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(14, 4));

            int offset = 1; // audio offset baseline
            if (packet.Type == HeaderType.VIDEO)
            {
                offset = 3;
                packet.UnitIndex = (int)((packet.Dword2 >> 0x15) & 0x7FF);
                packet.AdaptiveStreamIndex = (sbyte)buf[20] >> 5;
            }
            else
            {
                packet.UnitIndex = (int)((packet.Dword2 >> 0x18) & 0xFF);
            }

            if (packet.HasNalu)
            {
                // Unknown ushort at 18, then 2 bytes NALU after an extra byte
                offset += 3;
            }

            if (packet.Type == HeaderType.AUDIO && string.Equals(hostType, "PS5", StringComparison.OrdinalIgnoreCase))
            {
                offset += 1;
            }

            int dataStart = 18 + offset;
            if (dataStart <= buf.Length)
            {
                packet.Data = buf.AsSpan(dataStart).ToArray();
            }

            // derive meta
            if (packet.Type == HeaderType.VIDEO)
            {
                int total = (int)(((packet.Dword2 >> 0x0A) & 0x7FF) + 1);
                int fec = (int)(packet.Dword2 & 0x3FF);
                int src = total - fec;
                packet.UnitsTotal = total;
                packet.UnitsFec = fec;
                packet.UnitsSrc = src;
            }
            else
            {
                int d2low = (int)(packet.Dword2 & 0xFFFF);
                int total = (int)(((packet.Dword2 >> 0x10) & 0xFF) + 1);
                int fec = (d2low >> 4) & 0x0F;
                int src = d2low & 0x0F;
                int size = d2low >> 8;
                packet.UnitsTotal = total;
                packet.UnitsFec = fec;
                packet.UnitsSrc = src;
                packet.AudioUnitSize = size;
            }
            return true;
        }
    }
}


