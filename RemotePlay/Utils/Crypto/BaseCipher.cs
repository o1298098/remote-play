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
                ulong keyPosLong = (ulong)KeystreamIndex * KeyStreamLen;
                if (keyPosLong > uint.MaxValue)
                {
                    keyPosLong = uint.MaxValue;
                }
                uint keyPos = (uint)keyPosLong;
                var keyStream = GetKeyStream(BaseKey!, BaseIv!, keyPos, KeyStreamLen);
                Keystreams.Add((KeystreamIndex, keyStream));
                KeystreamIndex++;
            }
        }

        protected byte[] GetKeyStream(uint keyPos, int dataLen)
        {
            NextKeyStream();
            // Remove old blocks
            for (int i = 0; i < Keystreams.Count; i++)
            {
                var ksIndex = Keystreams[i].Item1;
                uint streamIndex = keyPos / KeyStreamLen;
                if (streamIndex > int.MaxValue)
                {
                    break;
                }
                if ((int)streamIndex > ksIndex)
                {
                    Keystreams.RemoveAt(i);
                    i--;
                }
                else break;
            }

            if (Keystreams.Count == 0) return Array.Empty<byte>();

            bool requiresAdditional = false;
            int startPos = (int)(keyPos % KeyStreamLen);
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
        protected static byte[] CounterAdd(int counter, byte[] iv)
        {
            byte[] result = new byte[iv.Length];
            iv.CopyTo(result, 0);
            for (int i = 0; i < result.Length; i++)
            {
                long add = (long)result[i] + counter;
                result[i] = (byte)(add & 0xFF);
                counter = (int)(add >> 8);
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

        protected static byte[] GetKeyStream(byte[] key, byte[] initVector, uint keyPos, int length)
        {
            int padding = (int)(keyPos % 16);
            keyPos -= (uint)padding;
            int keyStreamLen = ((padding + length + 16 - 1) / 16) * 16;
            byte[] buffer = new byte[keyStreamLen];
            
            ulong counterStartUlong = (keyPos / 16) + 1;
            if (counterStartUlong > int.MaxValue)
            {
                counterStartUlong = int.MaxValue;
            }
            int counterStart = (int)counterStartUlong;
            for (int i = 0; i < keyStreamLen / 16; i++)
            {
                if (counterStart > int.MaxValue - i)
                {
                    throw new OverflowException($"Counter overflow: counterStart={counterStart}, i={i}");
                }
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


