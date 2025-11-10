using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using System.Security.Cryptography;

namespace RemotePlay.Utils.Crypto
{
    public class SessionCipher
    {
        private readonly string _hostType;
        private IBufferedCipher _encCipher;
        private IBufferedCipher _decCipher;
        private readonly byte[] _key;
        private readonly byte[] _nonce;
        private long _encCounter;
        private long _decCounter;

        public SessionCipher(string hostType, byte[] key, byte[] nonce, int counter = 0)
        {
            _hostType = hostType;
            _key = key;
            _nonce = nonce;
            _encCounter = counter;
            _decCounter = counter;

            _encCipher = GetCipher(_key, GetIv(_nonce, _encCounter, _hostType), true);
            _decCipher = GetCipher(_key, GetIv(_nonce, _decCounter, _hostType), false);
        }

        private static IBufferedCipher GetCipher(byte[] key, byte[] iv, bool forEncryption)
        {
            var cipher = new BufferedBlockCipher(new CfbBlockCipher(new AesEngine(), 128));
            cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(key), iv));
            return cipher;
        }

        private static byte[] GetIv(byte[] nonce, long counter, string hostType)
        {
            byte[] suffix = new byte[8];
            for (int i = 0; i < 8; i++)
                suffix[i] = (byte)((counter >> (56 - 8 * i)) & 0xFF);
            var ivInput = nonce.Concat(suffix).ToArray();
            using var hmac = new HMACSHA256(hostType.ToUpper() == "PS5" ? HMAC_KEY_PS5 : HMAC_KEY_PS4);
            return hmac.ComputeHash(ivInput).Take(16).ToArray();
        }

        public byte[] Encrypt(byte[] data, int? counter = null)
        {
            if (counter.HasValue)
            {
                var temp = GetCipher(_key, GetIv(_nonce, counter.Value, _hostType), true);
                return temp.DoFinal(data);
            }

            var enc = _encCipher.DoFinal(data);
            _encCounter++;
            _encCipher = GetCipher(_key, GetIv(_nonce, _encCounter, _hostType), true);
            return enc;
        }

        public byte[] Decrypt(byte[] data)
        {
            var dec = _decCipher.DoFinal(data);
            _decCounter++;
            _decCipher = GetCipher(_key, GetIv(_nonce, _decCounter, _hostType), false);
            return dec;
        }

        public long EncCounter => _encCounter;
        public long DecCounter => _decCounter;

        // HMAC keys for PS4 / PS5 (replace with actual)
        private static readonly byte[] HMAC_KEY_PS4 = Models.PlayStation.Key.HMAC_KEY_PS4;
        private static readonly byte[] HMAC_KEY_PS5 = Models.PlayStation.Key.HMAC_KEY_PS5;
    }
}
