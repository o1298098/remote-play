namespace RemotePlay.Utils.Crypto
{
    public class RemoteCipher : BaseCipher
    {
        public RemoteCipher(byte[] handshakeKey, byte[] secret) : base(handshakeKey, secret)
        {
            BaseIndex = 3;
            InitCipher();
        }

        public byte[] Decrypt(byte[] data, uint keyPos)
        {
            var keyStream = GetKeyStream(keyPos, data.Length);
            return DecryptEncrypt(BaseKey!, BaseIv!, keyPos, data, keyStream);
        }

        public bool VerifyGmac(byte[] data, uint keyPos, byte[] gmac)
        {
            var tag = GetGmac(data, keyPos);
            var result = tag.SequenceEqual(gmac);
            return result;
        }
    }
}
