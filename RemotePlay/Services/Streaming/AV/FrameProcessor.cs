using Microsoft.Extensions.Logging;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils;
using System;
using System.Collections.Generic;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 帧处理器
    /// 负责帧的组装和 FEC 恢复
    /// </summary>
    public class FrameProcessor
    {
        private readonly ILogger<FrameProcessor>? _logger;
        private byte[]? _frameBuf;
        private int _frameBufSize;
        private int _bufSizePerUnit;
        private int _bufStridePerUnit;
        private int _unitsSourceExpected;
        private int _unitsFecExpected;
        private int _unitsSourceReceived;
        private int _unitsFecReceived;
        private FrameUnit[]? _unitSlots;
        private int _unitSlotsSize;
        private bool _flushed;
        private readonly StreamStats2 _streamStats = new StreamStats2();
        private readonly object _lock = new();
        
        // Packet stats for congestion control (类似chiaki的packet_stats)
        private Congestion.PacketStats? _packetStats;

        private const int UNIT_SLOTS_MAX = 512;
        private const int VIDEO_BUFFER_PADDING_SIZE = 64;

        private class FrameUnit
        {
            public int DataSize { get; set; }
        }

        public FrameProcessor(ILogger<FrameProcessor>? logger = null)
        {
            _logger = logger;
            Reset();
        }

        public void Reset()
        {
            lock (_lock)
            {
                _frameBuf = null;
                _frameBufSize = 0;
                _bufSizePerUnit = 0;
                _bufStridePerUnit = 0;
                _unitsSourceExpected = 0;
                _unitsFecExpected = 0;
                _unitsSourceReceived = 0;
                _unitsFecReceived = 0;
                _unitSlots = null;
                _unitSlotsSize = 0;
                _flushed = true;
                _streamStats.Reset();
            }
        }

        /// <summary>
        /// 分配帧缓冲区
        /// </summary>
        public bool AllocFrame(AVPacketWrapper packet)
        {
            lock (_lock)
            {
                if (packet.UnitsTotal < packet.UnitsFec)
                {
                    _logger?.LogError("Packet has units_total < units_fec");
                    return false;
                }

                _flushed = false;
                _unitsSourceExpected = packet.UnitsTotal - packet.UnitsFec;
                _unitsFecExpected = packet.UnitsFec;
                if (_unitsFecExpected < 1)
                    _unitsFecExpected = 1;

                _bufSizePerUnit = packet.Data.Length;
                
                // 视频包：前两个字节是大小扩展
                if (packet.Type == HeaderType.VIDEO && packet.UnitIndex < _unitsSourceExpected)
                {
                    if (packet.Data.Length < 2)
                    {
                        _logger?.LogError("Packet too small to read buf size extension");
                        return false;
                    }
                    ushort sizeExtension = (ushort)((packet.Data[0] << 8) | packet.Data[1]);
                    _bufSizePerUnit += sizeExtension;
                }

                _bufStridePerUnit = ((_bufSizePerUnit + 0xF) / 0x10) * 0x10;

                if (_bufSizePerUnit == 0)
                {
                    _logger?.LogError("Frame Processor doesn't handle empty units");
                    return false;
                }

                _unitsSourceReceived = 0;
                _unitsFecReceived = 0;

                int unitSlotsSizeRequired = _unitsSourceExpected + _unitsFecExpected;
                if (unitSlotsSizeRequired > UNIT_SLOTS_MAX)
                {
                    _logger?.LogError("Packet suggests more than {Max} unit slots", UNIT_SLOTS_MAX);
                    return false;
                }

                if (unitSlotsSizeRequired != _unitSlotsSize)
                {
                    _unitSlots = new FrameUnit[unitSlotsSizeRequired];
                    _unitSlotsSize = unitSlotsSizeRequired;
                }

                for (int i = 0; i < _unitSlotsSize; i++)
                {
                    _unitSlots[i] = new FrameUnit();
                }

                int frameBufSizeRequired = _unitSlotsSize * _bufStridePerUnit;
                if (_frameBufSize < frameBufSizeRequired)
                {
                    _frameBuf = new byte[frameBufSizeRequired + VIDEO_BUFFER_PADDING_SIZE];
                    _frameBufSize = frameBufSizeRequired;
                }

                Array.Clear(_frameBuf, 0, frameBufSizeRequired + VIDEO_BUFFER_PADDING_SIZE);

                return true;
            }
        }

        /// <summary>
        /// 添加 unit 数据
        /// </summary>
        public bool PutUnit(AVPacketWrapper packet)
        {
            lock (_lock)
            {
                if (packet.UnitIndex >= _unitSlotsSize)
                {
                    _logger?.LogError("Packet's unit index {Index} is too high (max: {Max})", packet.UnitIndex, _unitSlotsSize);
                    return false;
                }

                if (packet.Data.Length == 0)
                {
                    _logger?.LogWarning("Unit is empty");
                    return false;
                }

                if (packet.Data.Length > _bufSizePerUnit)
                {
                    _logger?.LogWarning("Unit is bigger than pre-calculated size! {Actual} > {Expected}", 
                        packet.Data.Length, _bufSizePerUnit);
                    return false;
                }

                FrameUnit unit = _unitSlots[packet.UnitIndex];
                if (unit.DataSize > 0)
                {
                    _logger?.LogWarning("Received duplicate unit at index {Index}", packet.UnitIndex);
                    return false;
                }

                unit.DataSize = packet.Data.Length;
                if (!_flushed)
                {
                    int offset = packet.UnitIndex * _bufStridePerUnit;
                    Array.Copy(packet.Data, 0, _frameBuf, offset, packet.Data.Length);
                }

                if (packet.UnitIndex < _unitsSourceExpected)
                    _unitsSourceReceived++;
                else
                    _unitsFecReceived++;

                return true;
            }
        }

        /// <summary>
        /// 检查是否可以刷新
        /// </summary>
        public bool FlushPossible()
        {
            lock (_lock)
            {
                return _unitsSourceReceived + _unitsFecReceived >= _unitsSourceExpected;
            }
        }

        /// <summary>
        /// 刷新帧
        /// </summary>
        public FlushResult Flush(out byte[]? frame, out int frameSize)
        {
            frame = null;
            frameSize = 0;

            lock (_lock)
            {
                if (_unitsSourceExpected == 0 || _flushed)
                {
                    return FlushResult.Failed;
                }

                FlushResult result = FlushResult.Success;

                // 如果源包不足，尝试 FEC 恢复
                if (_unitsSourceReceived < _unitsSourceExpected)
                {
                    bool fecSuccess = TryFecRecovery();
                    if (fecSuccess)
                        result = FlushResult.FecSuccess;
                    else
                        result = FlushResult.FecFailed;
                }

                // 组装帧数据
                var frameParts = new List<byte[]>();
                int totalSize = 0;

                for (int i = 0; i < _unitsSourceExpected; i++)
                {
                    FrameUnit unit = _unitSlots[i];
                    if (unit.DataSize == 0)
                    {
                        _logger?.LogWarning("Missing unit {Index}", i);
                        continue;
                    }

                    if (unit.DataSize < 2)
                    {
                        _logger?.LogError("Saved unit has size < 2");
                        continue;
                    }

                    // 跳过前两个字节（大小扩展）
                    int partSize = unit.DataSize - 2;
                    int offset = i * _bufStridePerUnit;
                    byte[] part = new byte[partSize];
                    Array.Copy(_frameBuf, offset + 2, part, 0, partSize);
                    frameParts.Add(part);
                    totalSize += partSize;
                }

                if (totalSize == 0)
                {
                    return FlushResult.Failed;
                }

                frame = new byte[totalSize];
                int currentOffset = 0;
                foreach (var part in frameParts)
                {
                    Array.Copy(part, 0, frame, currentOffset, part.Length);
                    currentOffset += part.Length;
                }

                frameSize = totalSize;
                _streamStats.RecordFrame((ulong)totalSize);
                _flushed = true;

                return result;
            }
        }

        /// <summary>
        /// FEC 恢复
        /// </summary>
        private bool TryFecRecovery()
        {
            _logger?.LogInformation("Frame Processor received {SourceReceived}+{FecReceived} / {SourceExpected}+{FecExpected} units, attempting FEC",
                _unitsSourceReceived, _unitsFecReceived, _unitsSourceExpected, _unitsFecExpected);

            int erasuresCount = (_unitsSourceExpected + _unitsFecExpected) - (_unitsSourceReceived + _unitsFecReceived);
            var erasures = new List<int>();

            for (int i = 0; i < _unitsSourceExpected + _unitsFecExpected; i++)
            {
                FrameUnit slot = _unitSlots[i];
                if (slot.DataSize == 0)
                {
                    erasures.Add(i);
                }
            }

            if (erasures.Count != erasuresCount)
            {
                _logger?.LogError("Erasure count mismatch: expected {Expected}, got {Actual}", erasuresCount, erasures.Count);
                return false;
            }

            // 使用 FecRecovery 进行恢复
            var packets = new List<byte[]>();
            for (int i = 0; i < _unitSlotsSize; i++)
            {
                if (_unitSlots[i].DataSize > 0)
                {
                    int offset = i * _bufStridePerUnit;
                    byte[] data = new byte[_unitSlots[i].DataSize];
                    Array.Copy(_frameBuf, offset, data, 0, data.Length);
                    packets.Add(data);
                }
                else
                {
                    packets.Add(Array.Empty<byte>());
                }
            }

            // 如果 logger 为 null，使用 NullLogger
            ILogger logger = _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameProcessor>.Instance;
            bool recovered = FecRecovery.TryRecover(packets, erasures, _unitsSourceExpected, _unitsFecExpected, logger);
            
            if (recovered)
            {
                _logger?.LogInformation("FEC successful");

                // 恢复 unit sizes
                for (int i = 0; i < _unitsSourceExpected; i++)
                {
                    FrameUnit slot = _unitSlots[i];
                    int offset = i * _bufStridePerUnit;
                    ushort padding = (ushort)((_frameBuf[offset] << 8) | _frameBuf[offset + 1]);
                    if (padding >= _bufSizePerUnit)
                    {
                        _logger?.LogError("Padding in unit ({Padding}) is larger or equals to the whole unit size ({Size})",
                            padding, _bufSizePerUnit);
                        continue;
                    }
                    slot.DataSize = _bufSizePerUnit - padding;
                }
            }
            else
            {
                _logger?.LogError("FEC failed");
            }

            return recovered;
        }

        public StreamStats2 GetStreamStats()
        {
            return _streamStats;
        }

        public (ulong frames, ulong bytes) GetAndResetStreamStats()
        {
            return _streamStats.GetAndReset();
        }

        /// <summary>
        /// 设置包统计（用于拥塞控制）
        /// </summary>
        public void SetPacketStats(Congestion.PacketStats? packetStats)
        {
            _packetStats = packetStats;
        }

        /// <summary>
        /// 报告 packet stats（用于拥塞控制）
        /// 类似chiaki的chiaki_frame_processor_report_packet_stats
        /// 应该在检测到新帧时调用（在AllocFrame之前）
        /// </summary>
        public void ReportPacketStats()
        {
            lock (_lock)
            {
                // 只有在有有效帧数据时才报告
                if (_unitsSourceExpected > 0 || _unitsFecExpected > 0)
                {
                    ulong received = (ulong)(_unitsSourceReceived + _unitsFecReceived);
                    ulong expected = (ulong)(_unitsSourceExpected + _unitsFecExpected);
                    ulong lost = expected > received ? expected - received : 0;
                    
                    // 记录详细的统计信息（用于调试固定丢失率问题）
                    if (expected > 0)
                    {
                        double lossRate = expected > 0 ? (double)lost / expected : 0;
                        _logger?.LogDebug(
                            "FrameProcessor stats: source_expected={SourceExpected}, source_received={SourceReceived}, " +
                            "fec_expected={FecExpected}, fec_received={FecReceived}, " +
                            "total_expected={TotalExpected}, total_received={TotalReceived}, lost={Lost}, loss_rate={LossRate:P2}",
                            _unitsSourceExpected, _unitsSourceReceived,
                            _unitsFecExpected, _unitsFecReceived,
                            expected, received, lost, lossRate);
                    }
                    
                    // ✅ 推送 Generation 统计（类似 chiaki_packet_stats_push_generation）
                    _packetStats?.PushGeneration(received, lost);
                }
            }
        }

        /// <summary>
        /// 获取并重置 packet stats（用于拥塞控制）
        /// 注意：现在统计由 PacketStats 统一管理，这个方法保留用于兼容性
        /// </summary>
        [Obsolete("统计现在由 PacketStats 统一管理，请使用 PacketStats.GetAndReset")]
        public (ulong received, ulong lost) GetAndResetPacketStats()
        {
            // 返回空值，因为统计现在由 PacketStats 统一管理
            return (0, 0);
        }
    }

    public enum FlushResult
    {
        Success = 0,
        FecSuccess = 1,
        FecFailed = 2,
        Failed = 3
    }

    public class StreamStats2
    {
        private ulong _frames = 0;
        private ulong _bytes = 0;
        private readonly object _lock = new();

        public void Reset()
        {
            lock (_lock)
            {
                _frames = 0;
                _bytes = 0;
            }
        }

        public void RecordFrame(ulong size)
        {
            lock (_lock)
            {
                _frames++;
                _bytes += size;
            }
        }

        public (ulong frames, ulong bytes) GetAndReset()
        {
            lock (_lock)
            {
                var result = (_frames, _bytes);
                _frames = 0;
                _bytes = 0;
                return result;
            }
        }

        public ulong CalculateBitrate(ulong framerate)
        {
            lock (_lock)
            {
                if (_frames == 0)
                    return 0;
                return (_bytes * 8 * framerate) / _frames;
            }
        }

        public double GetBitrateMbps(ulong framerate)
        {
            ulong bps = CalculateBitrate(framerate);
            return bps / 1000000.0;
        }
    }
}

