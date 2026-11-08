namespace RemotePlay.Utils.Crypto
{
    public class RemoteCipher : BaseCipher
    {
        private readonly object _epochLock = new();
        private uint _lastKeyPos;
        private long _wrapCount;
        // 实测 PS5 回绕后按 64 位位置继续加密；回绕时 IngestPipeline 仍会用音频数据再校验一次
        private volatile bool _extendedKeyPos = true;

        public RemoteCipher(byte[] handshakeKey, byte[] secret) : base(handshakeKey, secret)
        {
            BaseIndex = 3;
            InitCipher();
        }

        /// <summary>
        /// 包头中的 32 位 key_pos 回绕的次数（约每 4 GB 数据一次）
        /// </summary>
        public int KeyPosWrapCount => (int)Interlocked.Read(ref _wrapCount);

        /// <summary>
        /// 回绕后的密钥流位置：false = 直接使用 32 位值；true = 回绕次数 × 2^32 + 32 位值
        /// </summary>
        public bool ExtendedKeyPos
        {
            get => _extendedKeyPos;
            set => _extendedKeyPos = value;
        }

        public byte[] Decrypt(byte[] data, uint keyPos)
        {
            long epoch = TrackEpoch(keyPos);
            return DecryptAt(data, keyPos, epoch, _extendedKeyPos);
        }

        /// <summary>
        /// 用指定的位置模型解密（不更新回绕跟踪），用于回绕后判断哪种模型正确
        /// </summary>
        public byte[] DecryptWithModel(byte[] data, uint keyPos, bool extended)
        {
            long epoch;
            lock (_epochLock)
            {
                epoch = EpochOf(keyPos);
            }
            return DecryptAt(data, keyPos, epoch, extended);
        }

        private long TrackEpoch(uint keyPos)
        {
            lock (_epochLock)
            {
                if (keyPos < _lastKeyPos && _lastKeyPos - keyPos > 0x80000000u)
                {
                    Interlocked.Increment(ref _wrapCount);
                    _lastKeyPos = keyPos;
                    return _wrapCount;
                }

                long epoch = EpochOf(keyPos);
                if (epoch == _wrapCount && keyPos > _lastKeyPos)
                {
                    _lastKeyPos = keyPos;
                }
                return epoch;
            }
        }

        // 回绕刚发生后，仍可能晚到属于上一轮（数值很大）的包
        private long EpochOf(uint keyPos)
        {
            if (keyPos > _lastKeyPos && keyPos - _lastKeyPos > 0x80000000u && _wrapCount > 0)
            {
                return _wrapCount - 1;
            }
            return _wrapCount;
        }

        private byte[] DecryptAt(byte[] data, uint keyPos, long epoch, bool extended)
        {
            ulong position = extended ? ((ulong)epoch << 32) + keyPos : keyPos;
            var keyStream = GetKeyStream(position, data.Length);
            return StrXor(data, keyStream);
        }

        public bool VerifyGmac(byte[] data, uint keyPos, byte[] gmac)
        {
            var tag = GetGmac(data, keyPos);
            var result = tag.SequenceEqual(gmac);
            return result;
        }
    }
}
