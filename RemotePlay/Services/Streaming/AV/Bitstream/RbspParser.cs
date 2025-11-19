using Microsoft.Extensions.Logging;
using System;

namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// RBSP (Raw Byte Sequence Payload) 解析器
    /// 用于解析 H.264/H.265 的 RBSP 编码数据
    /// 
    /// 参考 chiaki 的 vl_rbsp.h 实现
    /// 处理 emulation_prevention_three_byte (0x000003)
    /// </summary>
    public class RbspParser
    {
        private readonly ILogger<RbspParser>? _logger;
        private VlcParser _nal; // NAL unit 的 VLC 解析器
        private int _escaped; // 已转义的位数
        private int _removed; // 已移除的位数

        public RbspParser(VlcParser nal, uint numBits, ILogger<RbspParser>? logger = null)
        {
            _logger = logger;
            _escaped = 0;
            _removed = 0;

            Initialize(nal, numBits);
        }

        /// <summary>
        /// 初始化 RBSP 解析器
        /// </summary>
        private void Initialize(VlcParser nal, uint numBits)
        {
            int bitsLeft = nal.BitsLeft();
            int i;

            // 复制位置
            _nal = nal;

            _escaped = 0;
            _removed = 0;

            // 搜索 NAL unit 的结束位置（查找 startcode）
            while (_nal.SearchByte(numBits, 0x00))
            {
                if (_nal.PeekBits(24) == 0x000001 || _nal.PeekBits(32) == 0x00000001)
                {
                    _nal.Limit(bitsLeft - _nal.BitsLeft());
                    break;
                }
                _nal.EatBits(8);
            }

            // 搜索 emulation_prevention_three_byte (0x000003)
            int valid = _nal.ValidBits();
            for (i = 24; i <= valid; i += 8)
            {
                if ((_nal.PeekBits(i) & 0xFFFFFF) == 0x3)
                {
                    _nal.RemoveBits(i - 8, 8);
                    i += 8;
                }
            }

            valid = _nal.ValidBits();
            _escaped = (valid >= 16) ? 16 : ((valid >= 8) ? 8 : 0);
        }

        /// <summary>
        /// 填充位缓冲区，确保至少有 16 位可用
        /// </summary>
        public void FillBits()
        {
            int valid = _nal.ValidBits();
            int i, bits;

            // 如果已经有足够的位，直接返回
            if (valid >= 32)
                return;

            _nal.FillBits();

            // 如果剩余位数少于 24，直接返回
            if (_nal.BitsLeft() < 24)
                return;

            // 处理已转义的位
            valid -= _escaped;

            // 搜索 emulation_prevention_three_byte
            _escaped = 16;
            bits = _nal.ValidBits();
            for (i = valid + 24; i <= bits; i += 8)
            {
                if ((_nal.PeekBits(i) & 0xFFFFFF) == 0x3)
                {
                    _nal.RemoveBits(i - 8, 8);
                    _escaped = bits - i;
                    bits -= 8;
                    _removed += 8;
                    i += 8;
                }
            }
        }

        /// <summary>
        /// 读取 n 位无符号整数
        /// </summary>
        public uint ReadU(int n)
        {
            if (n == 0)
                return 0;

            FillBits();
            if (n > 16)
                FillBits();
            return _nal.GetUimsbf(n);
        }

        /// <summary>
        /// 读取无符号指数哥伦布编码 (Exp-Golomb)
        /// </summary>
        public uint ReadUE()
        {
            int bits = 0;
            FillBits();
            while (_nal.GetUimsbf(1) == 0)
                ++bits;
            return (1u << bits) - 1 + ReadU(bits);
        }

        /// <summary>
        /// 读取有符号指数哥伦布编码
        /// </summary>
        public int ReadSE()
        {
            uint codeNum = ReadUE();
            if ((codeNum & 1) != 0)
                return (int)((codeNum + 1) >> 1);
            else
                return -(int)(codeNum >> 1);
        }

        /// <summary>
        /// 检查是否还有更多数据
        /// </summary>
        public bool MoreData()
        {
            int bits, value;

            if (_nal.BitsLeft() > 8)
                return true;

            bits = _nal.ValidBits();
            value = (int)_nal.PeekBits(bits);
            if (value == 0 || value == (1 << (bits - 1)))
                return false;

            return true;
        }

        /// <summary>
        /// 获取底层 VLC 解析器（用于修改 bitstream）
        /// </summary>
        public VlcParser GetNal()
        {
            return _nal;
        }
    }
}

