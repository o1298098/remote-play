using System;

namespace RemotePlay.Services.Streaming.Congestion
{
    /// <summary>
    /// 包统计 - 类似 chiaki 的 ChiakiPacketStats
    /// 支持两种统计方式：
    /// 1. Generation 统计（帧内 unit 统计）- 用于视频
    /// 2. Sequence 统计（序列号统计）- 用于音频和网络包
    /// </summary>
    public class PacketStats
    {
        private readonly object _lock = new object();
        
        // Generation 统计（帧内 unit 统计）
        private ulong _genReceived = 0;
        private ulong _genLost = 0;
        
        // Sequence 统计（序列号统计）
        private ushort _seqMin = 0;
        private ushort _seqMax = 0;
        private ulong _seqReceived = 0;
        private bool _seqInitialized = false;

        /// <summary>
        /// 推送 Generation 统计（帧内 unit 统计）
        /// 类似 chiaki_packet_stats_push_generation
        /// </summary>
        public void PushGeneration(ulong received, ulong lost)
        {
            lock (_lock)
            {
                _genReceived += received;
                _genLost += lost;
            }
        }

        /// <summary>
        /// 推送 Sequence 统计（序列号统计）
        /// 类似 chiaki_packet_stats_push_seq
        /// </summary>
        public void PushSeq(ushort seqNum)
        {
            lock (_lock)
            {
                _seqReceived++;
                
                if (!_seqInitialized)
                {
                    _seqMin = seqNum;
                    _seqMax = seqNum;
                    _seqInitialized = true;
                }
                else
                {
                    if (IsSeq16Greater(seqNum, _seqMax))
                    {
                        _seqMax = seqNum;
                    }
                }
            }
        }

        /// <summary>
        /// 获取统计并可选重置
        /// 类似 chiaki_packet_stats_get
        /// </summary>
        public (ulong received, ulong lost) GetAndReset(bool reset = true)
        {
            lock (_lock)
            {
                ulong received = _genReceived;
                ulong lost = _genLost;

                // Sequence 统计
                if (_seqInitialized)
                {
                    // 计算序列号范围（处理溢出）
                    uint seqDiff = (uint)(_seqMax - _seqMin);
                    if (_seqMax < _seqMin)
                    {
                        // 处理溢出情况
                        seqDiff = (uint)((ushort.MaxValue - _seqMin) + _seqMax + 1);
                    }
                    
                    // 计算丢失的序列号
                    ulong seqLost = _seqReceived > seqDiff ? seqDiff : seqDiff - _seqReceived;
                    
                    received += _seqReceived;
                    lost += seqLost;
                }

                if (reset)
                {
                    Reset();
                }

                return (received, lost);
            }
        }

        /// <summary>
        /// 重置所有统计
        /// </summary>
        private void Reset()
        {
            _genReceived = 0;
            _genLost = 0;
            _seqMin = _seqMax;
            _seqReceived = 0;
        }

        /// <summary>
        /// 检查 seq1 是否大于 seq2（处理 16 位序列号溢出）
        /// </summary>
        private static bool IsSeq16Greater(ushort seq1, ushort seq2)
        {
            // 处理 16 位序列号溢出
            // 如果差值在 [1, 32767] 范围内，seq1 > seq2
            // 如果差值在 [32768, 65535] 范围内，seq1 < seq2（溢出）
            int diff = seq1 - seq2;
            return diff > 0 && diff <= 32767;
        }
    }
}

