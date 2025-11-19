using Microsoft.Extensions.Logging;
using System;

namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// VLC (Variable Length Code) 解析器
    /// 参考 chiaki 的 vl_vlc 实现
    /// 使用 64 位缓冲区进行高效的位读取
    /// </summary>
    public class VlcParser
    {
        private readonly ILogger<VlcParser>? _logger;
        private ulong _buffer; // 64 位缓冲区
        private int _invalidBits; // 无效位数（从高位开始）
        internal byte[] _data; // 内部访问，供 RbspParser 使用
        internal int _dataOffset;
        internal int _endOffset;
        private int _bytesLeft; // 剩余字节数

        public VlcParser(byte[] data, int startOffset, int length, ILogger<VlcParser>? logger = null)
        {
            _logger = logger;
            _data = data;
            _dataOffset = startOffset;
            _endOffset = startOffset + length;
            _bytesLeft = length;
            _buffer = 0;
            _invalidBits = 32; // 初始时 32 位无效

            AlignDataPtr();
            FillBits();
        }

        /// <summary>
        /// 对齐数据指针到 4 字节边界
        /// </summary>
        private void AlignDataPtr()
        {
            while (_dataOffset < _endOffset && (_dataOffset & 3) != 0)
            {
                _buffer |= (ulong)_data[_dataOffset] << (24 + _invalidBits);
                _dataOffset++;
                _invalidBits -= 8;
            }
        }

        /// <summary>
        /// 填充位缓冲区，确保至少有 32 位有效
        /// </summary>
        public void FillBits()
        {
            while (_invalidBits > 0)
            {
                int bytesLeft = _endOffset - _dataOffset;
                if (bytesLeft == 0)
                    return;

                if (bytesLeft >= 4)
                {
                    // 读取整个 32 位字（大端序，网络字节序）
                    // chiaki 使用 ntohl，即网络字节序转主机字节序
                    // 在 C# 中，我们按大端序读取，然后转换为小端序（如果系统是小端序）
                    uint value = (uint)(_data[_dataOffset] << 24) |
                                 (uint)(_data[_dataOffset + 1] << 16) |
                                 (uint)(_data[_dataOffset + 2] << 8) |
                                 (uint)(_data[_dataOffset + 3]);

                    // ntohl 转换：网络字节序（大端）转主机字节序
                    // 如果系统是小端序，需要字节交换
                    if (BitConverter.IsLittleEndian)
                    {
                        value = (value >> 24) | 
                                ((value >> 8) & 0xFF00) | 
                                ((value << 8) & 0xFF0000) | 
                                (value << 24);
                    }

                    _buffer |= (ulong)value << _invalidBits;
                    _dataOffset += 4;
                    _invalidBits -= 32;
                    break;
                }
                else
                {
                    // 逐字节读取
                    while (_dataOffset < _endOffset)
                    {
                        _buffer |= (ulong)_data[_dataOffset] << (24 + _invalidBits);
                        _dataOffset++;
                        _invalidBits -= 8;
                    }
                }
            }
        }

        /// <summary>
        /// 获取有效位数
        /// </summary>
        public int ValidBits()
        {
            return 32 - _invalidBits;
        }

        /// <summary>
        /// 获取剩余总位数
        /// </summary>
        public int BitsLeft()
        {
            int bytesLeft = _endOffset - _dataOffset;
            bytesLeft += _bytesLeft;
            return bytesLeft * 8 + ValidBits();
        }

        /// <summary>
        /// 查看指定位数（不移除）
        /// </summary>
        public uint PeekBits(int numBits)
        {
            if (ValidBits() < numBits)
                FillBits();
            return (uint)(_buffer >> (64 - numBits));
        }

        /// <summary>
        /// 移除指定位数
        /// </summary>
        public void EatBits(int numBits)
        {
            _buffer <<= numBits;
            _invalidBits += numBits;
        }

        /// <summary>
        /// 读取指定位数的无符号整数
        /// </summary>
        public uint GetUimsbf(int numBits)
        {
            uint value = PeekBits(numBits);
            EatBits(numBits);
            return value;
        }

        /// <summary>
        /// 读取指定位数的有符号整数
        /// </summary>
        public int GetSimsbf(int numBits)
        {
            long value = (long)PeekBits(numBits);
            // 符号扩展
            if (numBits < 64 && (value & (1L << (numBits - 1))) != 0)
            {
                value |= ~((1L << numBits) - 1);
            }
            EatBits(numBits);
            return (int)value;
        }

        /// <summary>
        /// 搜索特定字节值（字节边界对齐）
        /// </summary>
        public bool SearchByte(uint numBits, byte value)
        {
            // 确保在字节边界
            if (ValidBits() % 8 != 0)
                return false;

            // 清空位缓冲区
            while (ValidBits() > 0)
            {
                if (PeekBits(8) == value)
                {
                    FillBits();
                    return true;
                }
                EatBits(8);
                if (numBits != 0xFFFFFFFF)
                {
                    numBits -= 8;
                    if (numBits == 0)
                        return false;
                }
            }

            // 搜索字节缓冲区
            while (true)
            {
                if (_dataOffset >= _endOffset)
                    return false;

                if (_data[_dataOffset] == value)
                {
                    AlignDataPtr();
                    FillBits();
                    return true;
                }

                _dataOffset++;
                if (numBits != 0xFFFFFFFF)
                {
                    numBits -= 8;
                    if (numBits == 0)
                    {
                        AlignDataPtr();
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// 移除指定位数（从指定位置）
        /// </summary>
        public void RemoveBits(int pos, int numBits)
        {
            ulong mask = ~(ulong.MaxValue >> (pos + numBits));
            ulong lo = (_buffer & mask) << numBits;
            ulong hi = _buffer & (ulong.MaxValue << (64 - pos));
            _buffer = lo | hi;
            _invalidBits += numBits;
        }

        /// <summary>
        /// 限制剩余位数
        /// </summary>
        public void Limit(int bitsLeft)
        {
            FillBits();
            if (bitsLeft < ValidBits())
            {
                _invalidBits = 32 - bitsLeft;
                _buffer &= ~(ulong.MaxValue << (_invalidBits + 32));
                _endOffset = _dataOffset;
                _bytesLeft = 0;
            }
            else
            {
                _bytesLeft = (bitsLeft - ValidBits()) / 8;
                if (_bytesLeft < (_endOffset - _dataOffset))
                {
                    _endOffset = _dataOffset + _bytesLeft;
                    _bytesLeft = 0;
                }
                else
                {
                    _bytesLeft -= _endOffset - _dataOffset;
                }
            }
        }

        /// <summary>
        /// 获取当前数据指针位置
        /// </summary>
        public int GetDataOffset()
        {
            return _dataOffset;
        }

        /// <summary>
        /// 获取无效位数（用于修改 bitstream）
        /// </summary>
        public int GetInvalidBits()
        {
            return _invalidBits;
        }

        /// <summary>
        /// 设置缓冲区（用于修改 bitstream）
        /// </summary>
        public void SetBuffer(ulong buffer, int invalidBits)
        {
            _buffer = buffer;
            _invalidBits = invalidBits;
        }

        /// <summary>
        /// 获取缓冲区（用于修改 bitstream）
        /// </summary>
        public ulong GetBuffer()
        {
            return _buffer;
        }

        /// <summary>
        /// 获取当前数据指针位置（用于修改原始数据）
        /// </summary>
        public int GetDataPointerOffset()
        {
            return _dataOffset;
        }

        /// <summary>
        /// 修改原始数据缓冲区中的位（用于参考帧修改）
        /// 参考 chiaki 的实现：直接修改 rbsp.nal.data 指向的内存
        /// </summary>
        public void ModifyDataBuffer(ulong buffer, int invalidBits)
        {
            // 计算要修改的数据位置
            // chiaki 使用 d[-2] 和 d[-1] 来访问缓冲区中的两个 32 位字
            // 在 C# 中，我们需要计算对应的字节位置
            
            // 缓冲区中的高位和低位部分
            uint hi = (uint)(buffer >> 32);
            uint lo = (uint)(buffer & 0xFFFFFFFF);
            
            // 转换为网络字节序（大端序）
            if (BitConverter.IsLittleEndian)
            {
                hi = (hi >> 24) | ((hi >> 8) & 0xFF00) | ((hi << 8) & 0xFF0000) | (hi << 24);
                lo = (lo >> 24) | ((lo >> 8) & 0xFF00) | ((lo << 8) & 0xFF0000) | (lo << 24);
            }
            
            // 计算要写入的位置（data[-2] 和 data[-1]）
            // 注意：在 C# 中，我们需要确保不越界
            int writeOffset = _dataOffset - 8; // data[-2] 和 data[-1] 共 8 字节
            if (writeOffset >= 0 && writeOffset + 8 <= _data.Length)
            {
                _data[writeOffset] = (byte)(hi >> 24);
                _data[writeOffset + 1] = (byte)(hi >> 16);
                _data[writeOffset + 2] = (byte)(hi >> 8);
                _data[writeOffset + 3] = (byte)hi;
                _data[writeOffset + 4] = (byte)(lo >> 24);
                _data[writeOffset + 5] = (byte)(lo >> 16);
                _data[writeOffset + 6] = (byte)(lo >> 8);
                _data[writeOffset + 7] = (byte)lo;
            }
        }
    }
}

