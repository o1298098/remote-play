using System.Buffers.Binary;

namespace RemotePlay.Utils.Crypto
{
    public static class ProtoCodecRaw
    {
        /// <summary>
        /// 构建原始 BIG payload。
        /// launchSpec 必须先经过 EncodeLaunchSpecWithSession 处理。
        /// _cipher 未就绪时直接发送原始 bytes。
        /// </summary>
        public static byte[] BuildBigPayloadRaw(
            int clientVersion,
            byte[] sessionKey,
            byte[] launchSpec,
            byte[] encryptedKey,
            byte[]? ecdhPub = null,
            byte[]? ecdhSig = null
        )
        {
            sessionKey ??= Array.Empty<byte>();
            launchSpec ??= Array.Empty<byte>();
            encryptedKey ??= Array.Empty<byte>();
            ecdhPub ??= Array.Empty<byte>();
            ecdhSig ??= Array.Empty<byte>();

            // 计算总长度
            int totalLen = 4 + sessionKey.Length + launchSpec.Length + encryptedKey.Length + ecdhPub.Length + ecdhSig.Length;
            var buf = new byte[totalLen];

            int offset = 0;

            // 前 4 字节是 clientVersion（big endian）
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), (uint)clientVersion);
            offset += 4;

            // 依次复制 sessionKey / launchSpec / encryptedKey / ecdhPub / ecdhSig
            Buffer.BlockCopy(sessionKey, 0, buf, offset, sessionKey.Length);
            offset += sessionKey.Length;

            Buffer.BlockCopy(launchSpec, 0, buf, offset, launchSpec.Length);
            offset += launchSpec.Length;

            Buffer.BlockCopy(encryptedKey, 0, buf, offset, encryptedKey.Length);
            offset += encryptedKey.Length;

            if (ecdhPub.Length > 0)
            {
                Buffer.BlockCopy(ecdhPub, 0, buf, offset, ecdhPub.Length);
                offset += ecdhPub.Length;
            }

            if (ecdhSig.Length > 0)
            {
                Buffer.BlockCopy(ecdhSig, 0, buf, offset, ecdhSig.Length);
                offset += ecdhSig.Length;
            }

            return buf;
        }
    }
}
