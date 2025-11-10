using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemotePlay.Utils.Crypto
{
    public abstract class BaseCipher
    {
        protected readonly byte[] HandshakeKey;
        protected readonly byte[] Secret;

        protected byte[]? BaseKey;
        protected byte[]? BaseIv;
        protected byte[]? BaseGmacKey;
        protected byte[]? CurrentKey;
        protected int Index;
        protected int BaseIndex;
        protected List<(int, byte[])> Keystreams = new List<(int, byte[])>();
        protected int KeystreamIndex;

        public const int KeyStreamLen = 0x1000;

        protected BaseCipher(byte[] handshakeKey, byte[] secret)
        {
            HandshakeKey = handshakeKey;
            Secret = secret;
        }

        protected void InitCipher()
        {
            (BaseKey, BaseIv) = GetBaseKeyIv(Secret, HandshakeKey, BaseIndex);
            CurrentKey = BaseGmacKey = GetGmacKey(Index, BaseKey, BaseIv);
            NextKeyStream();
        }

        protected void NextKeyStream()
        {
            while (Keystreams.Count < 3)
            {
                int keyPos = KeystreamIndex * KeyStreamLen;
                var keyStream = GetKeyStream(BaseKey!, BaseIv!, keyPos, KeyStreamLen);
                Keystreams.Add((KeystreamIndex, keyStream));
                KeystreamIndex++;
            }
        }

        protected byte[] GetKeyStream(int keyPos, int dataLen)
        {
            NextKeyStream();
            // Remove old blocks
            for (int i = 0; i < Keystreams.Count; i++)
            {
                var ksIndex = Keystreams[i].Item1;
                if (keyPos / KeyStreamLen > ksIndex)
                {
                    Keystreams.RemoveAt(i);
                    i--;
                }
                else break;
            }

            if (Keystreams.Count == 0) return Array.Empty<byte>();

            bool requiresAdditional = false;
            int startPos = keyPos % KeyStreamLen;
            int endPos = startPos + dataLen;

            if (endPos > KeyStreamLen)
            {
                requiresAdditional = true;
                if (Keystreams.Count < 2) return Array.Empty<byte>();
                endPos = dataLen - (KeyStreamLen - startPos);
            }

            byte[] keyStream;
            if (requiresAdditional)
            {
                var first = Keystreams[0].Item2;
                Keystreams.RemoveAt(0);
                keyStream = first[startPos..].Concat(Keystreams[0].Item2[..endPos]).ToArray();
            }
            else
            {
                keyStream = Keystreams[0].Item2[startPos..endPos];
            }

            return keyStream;
        }

        public byte[] GetGmac(byte[] data, int keyPos)
        {
            var initVector = CounterAdd(keyPos / 16, BaseIv!);
            int index = keyPos > 0 ? (keyPos - 1) / 45000 : 0;
            byte[] key;
            if (index > Index)
            {
                Index = index;
                key = GenNewKey();
            }
            else if (index < Index)
            {
                key = GetGmacKey(index, BaseKey!, BaseIv!);
            }
            else
            {
                key = CurrentKey!;
            }

            return GetGmacTag(data, key, initVector);
        }

        protected byte[] GenNewKey()
        {
            if (BaseGmacKey == null) throw new Exception("Base GMAC Key is null");
            CurrentKey = GetGmacKey(Index, BaseGmacKey, BaseIv!);
            return CurrentKey;
        }

        // -------------------- Helpers --------------------
        protected static byte[] CounterAdd(int counter, byte[] iv)
        {
            byte[] result = new byte[iv.Length];
            iv.CopyTo(result, 0);
            for (int i = 0; i < result.Length; i++)
            {
                int add = result[i] + counter;
                result[i] = (byte)(add & 0xFF);
                counter = add >> 8;
                if (counter <= 0 || i >= 15) break;
            }
            return result;
        }
        public static byte[] StrXor(byte[] a, byte[] b)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (a.Length != b.Length)
                throw new ArgumentException("XOR 操作需要等长输入：a.Length != b.Length");

            var result = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }
            return result;
        }

        /// <summary>
        /// Return Decrypted or Encrypted packet. Two way. Essentially AES ECB.
        /// 双向加密/解密函数，如果 keyStream 为空则重新生成
        /// </summary>
        protected static byte[] DecryptEncrypt(
            byte[] key, 
            byte[] initVector, 
            int keyPos, 
            byte[] data, 
            byte[]? keyStream = null)
        {
            if (keyStream == null || keyStream.Length == 0)
            {
                keyStream = GetKeyStream(key, initVector, keyPos, data.Length);
            }
            return StrXor(data, keyStream);
        }
        protected static byte[] GetGmacKey(int gmacIndex, byte[] key, byte[] initVector)
        {
            gmacIndex *= 44910;
            var outArray = CounterAdd(gmacIndex, initVector);
            byte[] outKey = key.Concat(outArray).ToArray();
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(outKey);
            return StrXor(hash.Take(16).ToArray(), hash.Skip(16).Take(16).ToArray());
        }

        protected static (byte[], byte[]) GetBaseKeyIv(byte[] secret, byte[] handshakeKey, int index)
        {
            var keyIv = new byte[] { 0x01, (byte)index, 0x00 }
                .Concat(handshakeKey)
                .Concat(new byte[] { 0x01, 0x00 })
                .ToArray();
            using var hmac = new HMACSHA256(secret);
            var hash = hmac.ComputeHash(keyIv);
            return (hash.Take(16).ToArray(), hash.Skip(16).ToArray());
        }

        protected static byte[] GetGmacTag(byte[] data, byte[] key, byte[] iv)
        {
            // ✅ 使用 BouncyCastle 来支持 16 字节 nonce（Chiaki 使用的标准）
            // .NET 的 AesGcm 只支持 12 字节，但 Chiaki 使用 16 字节
            
            // 创建 AES-GCM cipher
            var cipher = new GcmBlockCipher(new AesEngine());
            
            // 设置 16 字节的 nonce 和 128 位 (16 字节) 的 tag
            var parameters = new AeadParameters(
                new KeyParameter(key), 
                128,  // tag size in bits (16 bytes)
                iv,   // 16-byte nonce
                null  // no additional authenticated data (AAD) - we put data as AAD below
            );
            
            // 初始化为加密模式
            cipher.Init(true, parameters);
            
            // Process AAD (associated authenticated data)
            // 在 GMAC 模式下，我们只处理 AAD，不加密任何数据
            cipher.ProcessAadBytes(data, 0, data.Length);
            
            // 完成计算并获取 tag
            byte[] outBuf = new byte[cipher.GetOutputSize(0)];
            int outLen = cipher.DoFinal(outBuf, 0);
            
            // 返回前 4 字节的 GMAC（协议仅使用 4 字节）
            return outBuf[..4];
        }

        protected static byte[] GetKeyStream(byte[] key, byte[] initVector, int keyPos, int length)
        {
            int padding = keyPos % 16;
            keyPos -= padding;
            int keyStreamLen = ((padding + length + 16 - 1) / 16) * 16;
            byte[] buffer = new byte[keyStreamLen];
            
            // 从下一个块开始 (key_pos/16 + 1)
            int counterStart = keyPos / 16 + 1;
            for (int i = 0; i < keyStreamLen / 16; i++)
            {
                CounterAdd(counterStart + i, initVector).CopyTo(buffer, i * 16);
            }
            
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(buffer, 0, buffer.Length);
            return encrypted[padding..(padding + length)];
        }
    }
}


