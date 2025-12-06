namespace RemotePlay.Utils.Crypto
{
    public class LocalCipher : BaseCipher
    {
        private uint _keyPos;

        public LocalCipher(byte[] handshakeKey, byte[] secret) : base(handshakeKey, secret)
        {
            BaseIndex = 2;
            _keyPos = 0;
            InitCipher();
        }

        public byte[] Encrypt(byte[] data)
        {
            var keyStream = GetKeyStream(_keyPos, data.Length);
            return DecryptEncrypt(BaseKey!, BaseIv!, _keyPos, data, keyStream);
        }

        /// <summary>
        /// 使用指定的 keyPos 加密数据（不推进当前 _keyPos）
        /// </summary>
        public byte[] EncryptAtKeyPos(byte[] data, uint keyPos)
        {
            var keyStream = GetKeyStream(keyPos, data.Length);
            return DecryptEncrypt(BaseKey!, BaseIv!, keyPos, data, keyStream);
        }

        public void AdvanceKeyPos(int advanceBy)
        {
            if (advanceBy < 0)
            {
                throw new ArgumentException($"advanceBy must be non-negative, got {advanceBy}", nameof(advanceBy));
            }
            _keyPos += (uint)advanceBy;
        }

        public byte[] GetGmac(byte[] data)
        {
            var tag = base.GetGmac(data, _keyPos);
            return tag;
        }

        /// <summary>
        /// 使用指定的 keyPos 计算 GMAC（用于加密包时，需要使用加密前的 keyPos）
        /// </summary>
        public byte[] GetGmacAtKeyPos(byte[] data, uint keyPos)
        {
            var tag = base.GetGmac(data, keyPos);
            return tag;
        }

        public uint KeyPos => _keyPos;
    }
}
