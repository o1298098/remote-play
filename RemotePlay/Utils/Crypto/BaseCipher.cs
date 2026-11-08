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

        public const int KeyStreamLen = 0x1000;

        // 按块号缓存密钥流（AES-CTR 中每个位置的密钥流是确定的），与包到达顺序无关
        private const int MaxCachedKeyStreamBlocks = 8;
        private readonly Dictionary<ulong, byte[]> _keyStreamBlocks = new();
        private readonly Queue<ulong> _keyStreamBlockOrder = new();
        private readonly object _keyStreamLock = new();

        protected BaseCipher(byte[] handshakeKey, byte[] secret)
        {
            HandshakeKey = handshakeKey;
            Secret = secret;
        }

        protected void InitCipher()
        {
            (BaseKey, BaseIv) = GetBaseKeyIv(Secret, HandshakeKey, BaseIndex);
            CurrentKey = BaseGmacKey = GetGmacKey(Index, BaseKey, BaseIv);
        }

        protected byte[] GetKeyStream(ulong keyPos, int dataLen)
        {
            var result = new byte[dataLen];
            lock (_keyStreamLock)
            {
                int written = 0;
                while (written < dataLen)
                {
                    ulong pos = keyPos + (ulong)written;
                    ulong block = pos / KeyStreamLen;
                    int offset = (int)(pos % KeyStreamLen);
                    int count = Math.Min(KeyStreamLen - offset, dataLen - written);
                    System.Buffer.BlockCopy(GetKeyStreamBlock(block), offset, result, written, count);
                    written += count;
                }
            }
            return result;
        }

        private byte[] GetKeyStreamBlock(ulong block)
        {
            if (_keyStreamBlocks.TryGetValue(block, out var cached))
            {
                return cached;
            }

            var stream = GetKeyStream(BaseKey!, BaseIv!, block * KeyStreamLen, KeyStreamLen);
            _keyStreamBlocks[block] = stream;
            _keyStreamBlockOrder.Enqueue(block);
            while (_keyStreamBlockOrder.Count > MaxCachedKeyStreamBlocks)
            {
                _keyStreamBlocks.Remove(_keyStreamBlockOrder.Dequeue());
            }
            return stream;
        }

        public byte[] GetGmac(byte[] data, uint keyPos)
        {
            ulong counterValue = keyPos / 16;
            int counter = counterValue > int.MaxValue ? int.MaxValue : (int)counterValue;
            var initVector = CounterAdd(counter, BaseIv!);
            
            ulong indexValue = keyPos > 0 ? (keyPos - 1) / 45000 : 0;
            int index = indexValue > int.MaxValue ? int.MaxValue : (int)indexValue;
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
        protected static byte[] CounterAdd(long counter, byte[] iv)
        {
            byte[] result = new byte[iv.Length];
            iv.CopyTo(result, 0);
            for (int i = 0; i < result.Length; i++)
            {
                long add = result[i] + counter;
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
            uint keyPos, 
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
            long gmacIndexLong = (long)gmacIndex * 44910;
            if (gmacIndexLong > int.MaxValue)
            {
                gmacIndexLong = int.MaxValue;
            }
            int gmacIndexSafe = (int)gmacIndexLong;
            var outArray = CounterAdd(gmacIndexSafe, initVector);
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
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(
                new KeyParameter(key), 
                128,
                iv,
                null
            );
            
            cipher.Init(true, parameters);
            cipher.ProcessAadBytes(data, 0, data.Length);
            
            byte[] outBuf = new byte[cipher.GetOutputSize(0)];
            int outLen = cipher.DoFinal(outBuf, 0);
            
            return outBuf[..4];
        }

        protected static byte[] GetKeyStream(byte[] key, byte[] initVector, ulong keyPos, int length)
        {
            int padding = (int)(keyPos % 16);
            keyPos -= (ulong)padding;
            int keyStreamLen = ((padding + length + 16 - 1) / 16) * 16;
            byte[] buffer = new byte[keyStreamLen];
            
            long counterStart = (long)(keyPos / 16) + 1;
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


