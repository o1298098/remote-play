namespace RemotePlay.Utils.Crypto
{
    public class StreamCipher
    {
        private readonly LocalCipher _local;
        private readonly RemoteCipher _remote;

        public StreamCipher(byte[] handshakeKey, byte[] secret)
        {
            _local = new LocalCipher(handshakeKey, secret);
            _remote = new RemoteCipher(handshakeKey, secret);
        }

        public byte[] Encrypt(byte[] data) => _local.Encrypt(data);
        public byte[] EncryptAtKeyPos(byte[] data, int keyPos) => _local.EncryptAtKeyPos(data, keyPos);
        public void AdvanceKeyPos(int len) => _local.AdvanceKeyPos(len);
        public int KeyPos => _local.KeyPos;

        public byte[] Decrypt(byte[] data, int keyPos) => _remote.Decrypt(data, keyPos);
        public byte[] GetGmac(byte[] data) => _local.GetGmac(data);
        public byte[] GetGmacAtKeyPos(byte[] data, int keyPos) => _local.GetGmacAtKeyPos(data, keyPos);
        public bool VerifyGmac(byte[] data, int keyPos, byte[] gmac) => _remote.VerifyGmac(data, keyPos, gmac);
    }
}
