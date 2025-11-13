using Microsoft.Extensions.Logging;
using RemotePlay.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.AV
{
    public enum FrameProcessStatus
    {
        Success,
        Recovered,
        Frozen,
        Dropped
    }

    public readonly record struct FrameProcessInfo(
        int FrameIndex,
        FrameProcessStatus Status,
        bool RecoveredByFec,
        bool ReusedLastFrame,
        string? Reason);

    public sealed class AVStream
    {
        private readonly ILogger<AVStream> _logger;
        private readonly string _type; // "video" 或 "audio"
        private readonly Action<byte[]> _callbackDone;
        private readonly Action<int, int> _callbackCorrupt;
        private readonly Action<FrameProcessInfo>? _frameResultCallback;

        public byte[] Header { get; private set; }

        // 帧状态
        private readonly List<byte[]> _packets = new();
        private int _frame = -1;
        private int _lastUnit = -1;
        private int _lost = 0;
        private int _received = 0;
        private int _lastIndex = -1;
        private bool _frameBadOrder = false;
        private int _lastComplete = 0;
        private readonly List<int> _missing = new();
        private int _fallbackCounter = 0;

        private byte[]? _lastGoodVideoFrame;
        private int _fecAttempts = 0;
        private int _fecSuccess = 0;
        private int _fecFailures = 0;

        private readonly object _lock = new(); // 多线程安全锁

        public const string TYPE_VIDEO = "video";
        public const string TYPE_AUDIO = "audio";

        public AVStream(
            string avType,
            byte[] header,
            Action<byte[]> callbackDone,
            Action<int, int> callbackCorrupt,
            Action<FrameProcessInfo>? frameResultCallback,
            ILogger<AVStream> logger)
        {
            if (avType != TYPE_VIDEO && avType != TYPE_AUDIO)
                throw new ArgumentException("Invalid Type", nameof(avType));

            _type = avType;
            _callbackDone = callbackDone;
            _callbackCorrupt = callbackCorrupt;
            _frameResultCallback = frameResultCallback;
            _logger = logger;

            // 视频 header 添加 64 字节 padding
            if (avType == TYPE_VIDEO)
            {
                var padding = new byte[64];
                Header = new byte[header.Length + padding.Length];
                Buffer.BlockCopy(header, 0, Header, 0, header.Length);
                Buffer.BlockCopy(padding, 0, Header, header.Length, padding.Length);
            }
            else
            {
                Header = header;
            }
        }

        public void Handle(AVPacket packet, byte[] decryptedData)
        {
            lock (_lock)
            {
                // 更新计数器
                _received = (_received + 1) & 0xFFFF;

                // 检测新帧
                if (packet.FrameIndex != _frame)
                {
                    if (_lastComplete + 1 != packet.FrameIndex)
                        _callbackCorrupt(_lastComplete + 1, packet.FrameIndex);

                    SetNewFrame(packet);
                    _frame = packet.FrameIndex;
                }

                // 缺失包检测
                if (packet.UnitIndex != _lastUnit + 1)
                    HandleMissingPacket(packet.Index, packet.UnitIndex);

                _lastUnit += 1;

                // 添加数据
                AddPacketData(packet, decryptedData);

                // 处理 SRC / FEC
                if (!packet.IsFec)
                    HandleSrcPacket(packet);
                else
                    HandleFecPacket(packet);
            }
        }

        private void SetNewFrame(AVPacket packet)
        {
            _frameBadOrder = false;
            _missing.Clear();
            _packets.Clear();
            _frame = packet.FrameIndex;
            _lastUnit = -1;
            _fallbackCounter = 0;
        }

        private void HandleMissingPacket(int index, int unitIndex)
        {
            if (!_frameBadOrder)
            {
                _logger.LogWarning("⚠️ Received unit out of order: {Actual}, expected: {Expected}", unitIndex, _lastUnit + 1);
                _frameBadOrder = true;
            }

            for (int i = _lastUnit + 1; i < unitIndex; i++)
            {
                _packets.Add(Array.Empty<byte>());
                _missing.Add(i);
            }

            int missed = index - _lastIndex - 1;
            _lost = (_lost + (missed > 0 ? missed : 1)) & 0xFFFF;

            _lastUnit = unitIndex - 1;
        }

        private void TriggerFallback(AVPacket packet, string reason)
        {
            if (_type != TYPE_VIDEO)
                return;

            _fallbackCounter++;

            _frameBadOrder = true;
            _missing.Clear();
            _packets.Clear();
            _lastUnit = -1;

            _logger.LogWarning("⚠️ Video frame {Frame} fallback triggered: {Reason}", packet.FrameIndex, reason);

            if (_callbackCorrupt != null)
            {
                try
                {
                    int start = _lastComplete + 1;
                    if (start > packet.FrameIndex)
                        start = packet.FrameIndex;
                    _callbackCorrupt.Invoke(start, packet.FrameIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to invoke corrupt callback for frame {Frame}", packet.FrameIndex);
                }
            }

            bool reused = TryReplayLastFrame();
            var status = reused ? FrameProcessStatus.Frozen : FrameProcessStatus.Dropped;
            _frameResultCallback?.Invoke(new FrameProcessInfo(packet.FrameIndex, status, false, reused, reason));
        }

        private void AddPacketData(AVPacket packet, byte[] decryptedData)
        {
            if (_type == TYPE_AUDIO)
            {
                int size = packet.AudioUnitSize > 0 ? Math.Min(packet.AudioUnitSize, decryptedData.Length) : decryptedData.Length;
                var trimmed = new byte[size];
                Buffer.BlockCopy(decryptedData, 0, trimmed, 0, size);
                _packets.Add(trimmed);
            }
            else
            {
                _packets.Add(decryptedData);
            }
        }

        private void HandleSrcPacket(AVPacket packet)
        {
            bool shouldAssemble = false;

            if (packet.IsLastSrc && !_frameBadOrder)
                shouldAssemble = true;
            else if (!_frameBadOrder && _packets.Count >= packet.UnitsSrc)
            {
                int validPackets = _packets.Take(packet.UnitsSrc).Count(p => p != null && p.Length > 0);
                if (validPackets >= packet.UnitsSrc - 1)
                    shouldAssemble = true;
            }

            if (shouldAssemble && !_frameBadOrder)
                AssembleFrame(packet);
            else if (_type == TYPE_VIDEO && _frameBadOrder && packet.IsLastSrc && packet.UnitsFec == 0)
            {
                if (_packetLossSoftThresholdReached(packet))
                    TriggerFallback(packet, "missing source units with no FEC available");
            }
        }

        private bool _packetLossSoftThresholdReached(AVPacket packet)
        {
            if (_missing.Count == 0)
                return false;
            int allowableMissing = Math.Max(1, packet.UnitsSrc / 8);
            return _missing.Count > allowableMissing || _fallbackCounter >= 3;
        }

        private void HandleFecPacket(AVPacket packet)
        {
            if (!_frameBadOrder && _missing.Count == 0) return;
            if (!packet.IsLast) return;

            if (_missing.Count > packet.UnitsFec)
            {
                _fecAttempts++;
                _fecFailures++;
                _logger.LogWarning("⚠️ FEC insufficient: missing={Missing}, fec={Fec}", _missing.Count, packet.UnitsFec);
                if (_fallbackCounter >= 3 || _missing.Count > packet.UnitsSrc / 4)
                    TriggerFallback(packet, $"missing={_missing.Count}, fec={packet.UnitsFec}");
                return;
            }

            _fecAttempts++;
            bool recovered = FecRecovery.TryRecover(_packets, _missing, packet.UnitsSrc, packet.UnitsFec, _logger);
            if (recovered)
            {
                _fecSuccess++;
                _frameBadOrder = false;
                _missing.Clear();
                AssembleFrame(packet, true);
            }
            else if (_missing.Count > 0)
            {
                _fecFailures++;
                _logger.LogWarning("🚫 FEC recovery failed for frame {Frame}", packet.FrameIndex);
                TriggerFallback(packet, "FEC recovery failed");
            }
        }

        private void AssembleFrame(AVPacket packet, bool recoveredByFec = false)
        {
            if (_type == TYPE_VIDEO && (_packets.Count == 0 || _packets[0] == null || _packets[0].Length == 0))
            {
                _logger.LogWarning("⚠️ Frame {Frame} first packet missing, skipping", packet.FrameIndex);
                if (_fallbackCounter >= 2)
                    TriggerFallback(packet, "first unit missing");
                return;
            }

            byte[] frameData = ConcatPackets(_packets, packet.UnitsSrc, _type == TYPE_VIDEO);

            if (_type == TYPE_VIDEO && frameData.Length == 0)
            {
                _logger.LogWarning("⚠️ Video frame {Frame} is empty, skipping", packet.FrameIndex);
                if (_fallbackCounter >= 2)
                    TriggerFallback(packet, "assembled frame is empty");
                return;
            }

            if (_type == TYPE_VIDEO)
            {
                int finalLen = Header.Length + frameData.Length;
                var composedFrame = new byte[finalLen];
                Buffer.BlockCopy(Header, 0, composedFrame, 0, Header.Length);
                Buffer.BlockCopy(frameData, 0, composedFrame, Header.Length, frameData.Length);
                _lastGoodVideoFrame = composedFrame;
                _callbackDone(composedFrame);
                var status = recoveredByFec ? FrameProcessStatus.Recovered : FrameProcessStatus.Success;
                _frameResultCallback?.Invoke(new FrameProcessInfo(packet.FrameIndex, status, recoveredByFec, false, null));
            }
            else
            {
                _callbackDone(frameData);
            }

            _lastComplete = packet.FrameIndex;
        }

        private static byte[] ConcatPackets(List<byte[]> packets, int srcCount, bool skipFirstTwoBytes)
        {
            int total = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
            {
                var pkt = packets[i];
                if (pkt == null || pkt.Length == 0) continue;
                total += skipFirstTwoBytes && pkt.Length > 2 ? pkt.Length - 2 : pkt.Length;
            }

            if (total == 0) return Array.Empty<byte>();

            var buf = ArrayPool<byte>.Shared.Rent(total);
            int offset = 0;
            for (int i = 0; i < srcCount && i < packets.Count; i++)
            {
                var pkt = packets[i];
                if (pkt == null || pkt.Length == 0) continue;

                if (skipFirstTwoBytes && pkt.Length > 2)
                {
                    int len = pkt.Length - 2;
                    pkt.AsSpan(2, len).CopyTo(buf.AsSpan(offset, len));
                    offset += len;
                }
                else
                {
                    pkt.AsSpan().CopyTo(buf.AsSpan(offset, pkt.Length));
                    offset += pkt.Length;
                }
            }

            var result = new byte[total];
            Buffer.BlockCopy(buf, 0, result, 0, total);
            ArrayPool<byte>.Shared.Return(buf);
            return result;
        }

        public (int received, int lost) ConsumeAndResetCounters()
        {
            lock (_lock)
            {
                int received = _received;
                int lost = _lost;
                _received = 0;
                _lost = 0;
                return (received, lost);
            }
        }

        public (int attempts, int success, int failures) ConsumeAndResetFecCounters()
        {
            lock (_lock)
            {
                int attempts = _fecAttempts;
                int success = _fecSuccess;
                int failures = _fecFailures;
                _fecAttempts = 0;
                _fecSuccess = 0;
                _fecFailures = 0;
                return (attempts, success, failures);
            }
        }

        public int Lost => _lost;
        public int Received => _received;

        private bool TryReplayLastFrame()
        {
            if (_type != TYPE_VIDEO)
                return false;
            if (_lastGoodVideoFrame == null || _lastGoodVideoFrame.Length == 0)
                return false;

            var clone = new byte[_lastGoodVideoFrame.Length];
            Buffer.BlockCopy(_lastGoodVideoFrame, 0, clone, 0, clone.Length);
            _callbackDone(clone);
            return true;
        }
    }
}
