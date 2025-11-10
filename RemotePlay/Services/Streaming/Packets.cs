using System.Buffers.Binary;

namespace RemotePlay.Services.Streaming
{
    // Header.Type 定义
    public enum HeaderType : byte
    {
        CONTROL = 0x00,
        VIDEO = 0x02,
        AUDIO = 0x03,
        HANDSHAKE = 0x04,
        CONGESTION = 0x05,
        FEEDBACK_EVENT = 0x01,
        FEEDBACK_STATE = 0x06
    }

    // Chunk.Type 定义
    public enum ChunkType : byte
    {
        DATA = 0x00,
        INIT = 0x01,
        INIT_ACK = 0x02,
        DATA_ACK = 0x03,
        COOKIE = 0x0A,
        COOKIE_ACK = 0x0B
    }

    public static class PacketConst
    {
        public const int HeaderLength = 13; // type(1)+tag_remote(4)+gmac(4)+key_pos(4)
        public const int ARwnd = 0x019000; // 接收窗口
        public const byte OutboundStreams = 0x64; // 100
        public const byte InboundStreams = 0x64;  // 100
        public static readonly byte[] StreamStart = new byte[] { 0x00, 0x00, 0x00, 0x40, 0x01, 0x00, 0x00 };
    }

    public sealed class Packet
    {
        public HeaderType HeaderType { get; private set; }
        public ChunkType ChunkType { get; private set; }
        public uint TagRemote { get; private set; }
        public uint Gmac { get; private set; }
        public uint KeyPos { get; private set; }
        public int Flag { get; private set; }
        public int Channel { get; private set; }
        public uint Tsn { get; private set; }
        public byte[]? Data { get; private set; }
        public PacketParams Params { get; private set; } = new PacketParams();

        public static bool IsAv(byte firstByte)
        {
            var mask = (byte)(firstByte & 0x0F);
            return mask == (byte)HeaderType.VIDEO || mask == (byte)HeaderType.AUDIO;
        }

        // INIT payload: !IIHHI -> tag_local, a_rwnd, out_streams, in_streams, init_tsn
        public static byte[] CreateInit(uint tagLocal, uint initTsn)
        {
            var payload = new byte[4 + 4 + 2 + 2 + 4];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), tagLocal);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), PacketConst.ARwnd);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8, 2), PacketConst.OutboundStreams);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(10, 2), PacketConst.InboundStreams);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), initTsn);
            return CreateControlPacket(ChunkType.INIT, flag: 0, payload);
        }

        public static byte[] CreateStreamStart()
        {
            // 原样返回预设开场字节
            return PacketConst.StreamStart.ToArray();
        }

        // COOKIE: 直接承载 data
        // ✅ 修复：添加 tagLocal 和 tagRemote 参数
        public static byte[] CreateCookie(uint tagLocal, uint tagRemote, byte[] data)
        {
            var buf = new byte[PacketConst.HeaderLength + 1 + 1 + 2 + data.Length];
            
            // Header (13 bytes)
            buf[0] = (byte)HeaderType.CONTROL;
            // ✅ 设置 tag_remote（字节 1-4，big-endian）
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), tagRemote);
            // gmac (字节 5-8) 和 key_pos (字节 9-12) 保持为 0（这个阶段没有加密）
            
            // Chunk header (4 bytes)
            int o = PacketConst.HeaderLength;
            buf[o] = (byte)ChunkType.COOKIE;
            buf[o + 1] = 0; // flag
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o + 2, 2), (ushort)(data.Length + 4));
            
            // Chunk payload
            Buffer.BlockCopy(data, 0, buf, o + 4, data.Length);
            
            return buf;
        }

        // DATA: payload = !IHxxx + data
        public static byte[] CreateData(uint tsn, ushort channel, byte[] data)
        {
            var pl = new byte[4 + 2 + 3 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pl.AsSpan(0, 4), tsn);
            BinaryPrimitives.WriteUInt16BigEndian(pl.AsSpan(4, 2), channel);
            // 3 字节保留填 0
            Buffer.BlockCopy(data, 0, pl, 9, data.Length);
            return CreateControlPacket(ChunkType.DATA, flag: 1, pl);
        }

        // Overload with custom flag
        public static byte[] CreateData(uint tsn, ushort channel, int flag, byte[] data)
        {
            var pl = new byte[4 + 2 + 3 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pl.AsSpan(0, 4), tsn);
            BinaryPrimitives.WriteUInt16BigEndian(pl.AsSpan(4, 2), channel);
            Buffer.BlockCopy(data, 0, pl, 9, data.Length);
            return CreateControlPacket(ChunkType.DATA, flag: flag, pl);
        }

        private static byte[] CreateControlPacket(ChunkType type, int flag, byte[] payload)
        {
            // 头 13 字节 + chunk(1+1+2) + payload
            // 注意：chunk length 字段为 payload 长度 + 4（包含 chunk 头部）
            var buf = new byte[PacketConst.HeaderLength + 1 + 1 + 2 + payload.Length];
            // Header
            buf[0] = (byte)HeaderType.CONTROL;
            // tag_remote(4)/gmac(4)/key_pos(4) 现阶段置 0（待加密接入后回填）
            // Chunk
            int o = PacketConst.HeaderLength;
            buf[o] = (byte)type; // chunk type
            buf[o + 1] = (byte)flag; // flag
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o + 2, 2), (ushort)(payload.Length + 4));
            Buffer.BlockCopy(payload, 0, buf, o + 4, payload.Length);
            return buf;
        }

        public static Packet? Parse(byte[] msg)
        {
            // 最小合法控制包应为 13(头) + 4(chunk 头) = 17 字节
            if (msg == null || msg.Length < PacketConst.HeaderLength + 4) return null;
            var p = new Packet();
            p.HeaderType = (HeaderType)msg[0];
            p.TagRemote = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(1, 4));
            p.Gmac = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(5, 4));
            p.KeyPos = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(9, 4));

            var chunkType = (ChunkType)msg[PacketConst.HeaderLength];
            p.ChunkType = chunkType;
            p.Flag = msg[PacketConst.HeaderLength + 1];
            // 为了与既有解析逻辑兼容，这里不依赖 chunk length 字段，直接取剩余字节作为 payload
            var payloadOffset = PacketConst.HeaderLength + 4;
            var remaining = Math.Max(0, msg.Length - payloadOffset);
            var payload = remaining > 0
                ? msg.AsSpan(payloadOffset, remaining).ToArray()
                : Array.Empty<byte>();

            if (chunkType == ChunkType.DATA)
            {
                if (payload.Length >= 9)
                {
                    p.Tsn = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                    p.Channel = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2));
                    p.Data = payload.AsSpan(9).ToArray();
                }
            }
            else if (chunkType == ChunkType.INIT_ACK)
            {
                // INIT_ACK 解析 tag/tsn 与 data
                if (payload.Length >= 16)
                {
                    p.Params.Tag = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                    p.Params.Tsn = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(12, 4));
                    p.Params.Data = payload.AsSpan(16).ToArray(); // cookie data
                }
            }
            else if (chunkType == ChunkType.COOKIE_ACK)
            {
                p.Params.Data = payload;
            }
            else if (chunkType == ChunkType.DATA_ACK)
            {
                if (payload.Length >= 12)
                {
                    p.Params.Tsn = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                    p.Params.GapAckBlocks = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(8, 2));
                    p.Params.DupTsns = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(10, 2));
                }
            }
            return p;
        }

        // DATA_ACK payload: !IIHH -> tsn, a_rwnd, gap_acks, dup_tsns
        public static byte[] CreateDataAck(uint tsn)
        {
            var pl = new byte[4 + 4 + 2 + 2];
            BinaryPrimitives.WriteUInt32BigEndian(pl.AsSpan(0, 4), tsn);
            BinaryPrimitives.WriteUInt32BigEndian(pl.AsSpan(4, 4), PacketConst.ARwnd);
            BinaryPrimitives.WriteUInt16BigEndian(pl.AsSpan(8, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(pl.AsSpan(10, 2), 0);
            return CreateControlPacket(ChunkType.DATA_ACK, flag: 0, pl);
        }
    }

    public sealed class PacketParams
    {
        public uint Tag { get; set; }
        public byte[]? Data { get; set; }
        public int Tsn { get; set; }
        public int GapAckBlocks { get; set; }
        public int DupTsns { get; set; }
    }

    /// <summary>
    /// FeedbackPacket - 独立的 UDP 包，用于发送控制器状态和网络统计
    /// Python: FeedbackPacket + FeedbackHeader + FeedbackState/Congestion
    /// </summary>
    public static class FeedbackPacket
    {
        private const int HEADER_LENGTH = 12; // type(1) + sequence(2) + padding(1) + key_pos(4) + gmac(4)

        /// <summary>
        /// 创建 Congestion 包（网络拥塞统计）
        /// </summary>
        public static byte[] CreateCongestion(ushort sequence, byte[] congestionData, Utils.Crypto.StreamCipher cipher)
        {
            // 总长度 = header(12) + congestion data
            int totalLength = HEADER_LENGTH + congestionData.Length;
            var packet = new byte[totalLength];

            // 1. 保存当前的 key_pos（用于 header）
            var keyPos = (uint)cipher.KeyPos;

            // 2. 写入 header（gmac 先填 0）
            packet[0] = (byte)HeaderType.CONGESTION;  // type = 0x05
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1, 2), sequence);
            packet[3] = 0x00;  // padding
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), keyPos);

            // 3. 加密 congestion data（使用当前 key_pos）并写入
            var encrypted = cipher.Encrypt(congestionData);
            Buffer.BlockCopy(encrypted, 0, packet, HEADER_LENGTH, encrypted.Length);

            // 4. 计算 GMAC（gmac 字段需要为 0，但保留 key_pos）
            var temp = new byte[totalLength];
            Buffer.BlockCopy(packet, 0, temp, 0, totalLength);
            Array.Clear(temp, 8, 4); // 只清除 gmac 字段
            var gmacBytes = cipher.GetGmac(temp);
            var gmac = BinaryPrimitives.ReadUInt32BigEndian(gmacBytes);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), gmac);

            // 5. 推进 key_pos（仅推进 payload 长度）
            cipher.AdvanceKeyPos(congestionData.Length);

            return packet;
        }

        /// <summary>
        /// 创建 FeedbackState 包（控制器状态）
        /// </summary>
        public static byte[] CreateFeedbackState(ushort sequence, byte[] stateData, Utils.Crypto.StreamCipher cipher)
        {
            int totalLength = HEADER_LENGTH + stateData.Length;
            var packet = new byte[totalLength];

            // 1. 保存当前的 key_pos（用于 header）
            var keyPos = (uint)cipher.KeyPos;

            // 2. 写入 header（gmac 先填 0）
            packet[0] = (byte)HeaderType.FEEDBACK_STATE;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1, 2), sequence);
            packet[3] = 0x00; // padding
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), keyPos);

            // 3. 加密 payload（使用当前 key_pos）并写入
            var encrypted = cipher.Encrypt(stateData);
            Buffer.BlockCopy(encrypted, 0, packet, HEADER_LENGTH, encrypted.Length);

            // 5. 计算 GMAC（gmac 字段需要为 0）
            var temp = new byte[totalLength];
            Buffer.BlockCopy(packet, 0, temp, 0, totalLength);
            Array.Clear(temp, 8, 4); // 清零 gmac 字段
            var gmacBytes = cipher.GetGmac(temp);
            var gmac = BinaryPrimitives.ReadUInt32BigEndian(gmacBytes);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), gmac);

            // 6. 推进 key_pos（仅推进 payload 长度）
            cipher.AdvanceKeyPos(stateData.Length);

            return packet;
        }

        /// <summary>
        /// 创建 FeedbackEvent 包（按键事件）
        /// </summary>
        public static byte[] CreateEvent(ushort sequence, byte[] eventData, Utils.Crypto.StreamCipher cipher)
        {
            int totalLength = HEADER_LENGTH + eventData.Length;
            var packet = new byte[totalLength];

            // 1. 保存当前的 key_pos（用于 header）
            var keyPos = (uint)cipher.KeyPos;

            // 2. 写入 header（gmac 先填 0）
            packet[0] = (byte)HeaderType.FEEDBACK_EVENT;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1, 2), sequence);
            packet[3] = 0x00; // padding
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), keyPos);

            // 3. 加密 payload（使用当前 key_pos）并写入
            var encrypted = cipher.Encrypt(eventData);
            Buffer.BlockCopy(encrypted, 0, packet, HEADER_LENGTH, encrypted.Length);

            // 4. 计算 GMAC（gmac 字段需要为 0）
            var temp = new byte[totalLength];
            Buffer.BlockCopy(packet, 0, temp, 0, totalLength);
            Array.Clear(temp, 8, 4); // 清零 gmac 字段
            var gmacBytes = cipher.GetGmac(temp);
            var gmac = BinaryPrimitives.ReadUInt32BigEndian(gmacBytes);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), gmac);

            // 5. 推进 key_pos（仅推进 payload 长度）
            cipher.AdvanceKeyPos(eventData.Length);

            return packet;
        }
    }
}


