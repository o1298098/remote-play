using Microsoft.Extensions.Logging;
using RemotePlay.Utils;
using RemotePlay.Services.Streaming.AV.Bitstream;
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
        FecSuccess,
        FecFailed,
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

        // ✅ P 帧 fallback 相关（参考 chiaki-ng）
        private ReferenceFrameManager? _referenceFrameManager;
        private BitstreamParser? _bitstreamParser;
        private string? _detectedCodec;

        // ✅ 帧超时机制（参考 chiaki-ng，避免长时间等待不完整的帧）
        private DateTime _frameStartTime = DateTime.MinValue; // 帧开始时间
        private const int FRAME_TIMEOUT_MS = 500; // 帧超时时间（毫秒），参考视频帧率 30fps = 33ms/frame，设置 500ms 允许网络抖动和乱序
        private int _frameTimeoutDropped = 0; // 超时丢弃的帧数

        // ✅ 流统计（参考 chiaki-ng 的 ChiakiStreamStats）
        private readonly StreamStats _streamStats = new StreamStats();

        // ✅ 帧索引跟踪（参考 chiaki-ng：frame_index_prev / frames_lost）
        // _frameIndexCur：当前正在组装的帧索引（chiaki: frame_index_cur）
        // _frameIndexPrev：上一个至少部分解码的帧索引（chiaki: frame_index_prev）
        // _framesLost：累计丢失的帧数量（chiaki: frames_lost）
        private int _frameIndexCur = -1;
        private int _frameIndexPrev = -1;
        private int _framesLost = 0;
        private bool _currentFrameAssembled = false;

        // ✅ 音频启动逻辑（参考 chiaki-ng：frame_index_startup）
        // 启动期内忽略/减少 FEC 干预，避免重复包导致的爆音；成功若干帧后退出启动期
        private bool _audioStartup = true;
        private int _audioStartupSuccessFrames = 0;
        private const int AUDIO_STARTUP_SUCCESS_THRESHOLD = 3;

        private readonly object _lock = new(); // 多线程安全锁
        // 旧帧日志限流（避免热路径刷屏）
        private DateTime _lastOldPacketLogTime = DateTime.MinValue;
        private int _oldPacketSuppressed = 0;
        private static readonly TimeSpan OLD_PKT_LOG_INTERVAL = TimeSpan.FromSeconds(1);

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

                // ✅ 初始化参考帧管理器和 bitstream 解析器（参考 chiaki-ng）
                _referenceFrameManager = new ReferenceFrameManager(null); // Logger 可选
                // BitstreamParser 会在检测到 codec 后初始化
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
                // ✅ 旧帧包检测（环回安全）
                if (_frameIndexCur >= 0 && IsSeq16Older(packet.FrameIndex, _frameIndexCur))
                {
                    // 降级为 Debug，并做简单限流与聚合，避免热路径刷屏
                    var now = DateTime.UtcNow;
                    if (now - _lastOldPacketLogTime >= OLD_PKT_LOG_INTERVAL)
                    {
                        int suppressed = _oldPacketSuppressed;
                        _oldPacketSuppressed = 0;
                        _lastOldPacketLogTime = now;
                        _logger.LogDebug("Drop old frame packet: frame={Frame}, current={Current}, suppressed={Suppressed}", packet.FrameIndex, _frameIndexCur, suppressed);
                    }
                    else
                    {
                        _oldPacketSuppressed++;
                    }
                    return;
                }

                // 更新计数器
                _received = (_received + 1) & 0xFFFF;

                // 检测新帧
                if (packet.FrameIndex != _frame)
                {
                    // ✅ 只在视频流时报告帧索引跳跃，且只在已有完整帧的情况下报告
                    // 音频流的帧索引跳跃是正常的，不应该触发 corrupt callback
                    // 会话开始时（_lastComplete <= 0）的帧索引跳跃也是正常的
                    if (_type == TYPE_VIDEO && _lastComplete > 0 && _lastComplete + 1 != packet.FrameIndex)
                    {
                        _callbackCorrupt(_lastComplete + 1, packet.FrameIndex);
                    }

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

        // 16 位序列号（0..65535）环回安全“旧帧”判断：
        // 当 (seq - cur) 在模 2^16 下属于 (0x8001..0xFFFF) 时，seq 视为比 cur 更旧
        private static bool IsSeq16Older(int seq, int cur)
        {
            int diff = (seq - cur) & 0xFFFF;
            return diff > 0x8000;
        }

        private void SetNewFrame(AVPacket packet)
        {
            _frameBadOrder = false;
            _missing.Clear();
            _packets.Clear();
            _frame = packet.FrameIndex;
            _lastUnit = -1;
            _fallbackCounter = 0;
            _frameIndexCur = packet.FrameIndex;
            _currentFrameAssembled = false;
            
            // ✅ 仅对视频流记录帧开始时间（用于超时检查）
            // 音频流不需要超时检测，因为音频帧小且处理快，丢包会导致爆音
            if (_type == TYPE_VIDEO)
            {
                _frameStartTime = DateTime.UtcNow;
            }
            else
            {
                _frameStartTime = DateTime.MinValue; // 音频流不设置超时
                
                // ✅ 音频启动期退出条件之一：frame_index 超过半环（防止长时间保持启动状态）
                // 参考 chiaki-ng 的 frame_index_startup，采用简单阈值避免误判
                if (_audioStartup && packet.FrameIndex > (1 << 15))
                    _audioStartup = false;
            }
            
            // ✅ 如果帧索引跳跃过大，重置参考帧管理器（流可能已不同步）
            if (_type == TYPE_VIDEO && _lastComplete > 0)
            {
                int gap = packet.FrameIndex - _lastComplete;
                if (gap > 10) // 如果跳跃超过 10 帧，重置参考帧
                {
                    _logger.LogWarning("⚠️ 帧索引跳跃过大 ({Gap} 帧)，重置参考帧管理器", gap);
                    _referenceFrameManager?.Reset();
                }
            }

            // ✅ 统计丢失帧：如果新帧索引比上一个完整帧大于 1，说明中间丢帧
            if (_lastComplete > 0)
            {
                int lost = packet.FrameIndex - _lastComplete - 1;
                if (lost > 0)
                {
                    _framesLost += lost;
                    _logger.LogDebug("📉 检测到丢失帧：lost={Lost}, last_complete={Last}, current={Cur}", lost, _lastComplete, packet.FrameIndex);
                }
            }
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
            _frameStartTime = DateTime.MinValue; // ✅ 重置帧开始时间，避免影响下一个帧

            // ✅ 如果连续 fallback 次数过多，重置参考帧管理器
            if (_fallbackCounter >= 5)
            {
                _logger.LogWarning("⚠️ 连续 fallback 次数过多 ({Count})，重置参考帧管理器", _fallbackCounter);
                _referenceFrameManager?.Reset();
                _fallbackCounter = 0; // 重置计数器
            }

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
            // ✅ 检查帧超时（仅适用于视频流，参考 chiaki-ng，避免长时间等待不完整的帧）
            // 音频流不应该有超时检测，因为音频帧小且处理快，丢包会导致爆音
            if (_type == TYPE_VIDEO && _frameStartTime != DateTime.MinValue)
            {
                var elapsed = (DateTime.UtcNow - _frameStartTime).TotalMilliseconds;
                if (elapsed > FRAME_TIMEOUT_MS)
                {
                    _logger.LogWarning("⚠️ 帧 {Frame} 超时 ({Elapsed}ms > {Timeout}ms)，触发 fallback", 
                        packet.FrameIndex, elapsed, FRAME_TIMEOUT_MS);
                    _frameTimeoutDropped++;
                    TriggerFallback(packet, $"frame timeout ({elapsed:F0}ms)");
                    return;
                }
            }

            // ✅ 提前触发策略（对齐 chiaki-ng：“宁可丢帧，也不阻塞帧流”）
            // 如果已经检测到乱序/缺失，且缺失超过软阈值，并且没有可用 FEC，直接触发 fallback（无需等待本帧最后一个单元）
            if (_type == TYPE_VIDEO && _frameBadOrder && _missing.Count > 0 && packet.UnitsFec == 0)
            {
                if (_packetLossSoftThresholdReached(packet))
                {
                    _logger.LogWarning("⚠️ 提前触发 fallback（缺失超过阈值，避免等待到帧尾）: frame={Frame}, missing={Missing}, unitsSrc={UnitsSrc}",
                        packet.FrameIndex, _missing.Count, packet.UnitsSrc);
                    TriggerFallback(packet, "early fallback due to missing units beyond threshold");
                    return;
                }
            }

            bool shouldAssemble = false;

            // ✅ 音频流：即使有乱序也尝试组装（音频容错性更高）
            // 视频流：只有在没有乱序时才组装（乱序可能导致解码问题）
            if (_type == TYPE_AUDIO)
            {
                // 音频流：只要有足够的包就尝试组装，即使有乱序
                if (packet.IsLastSrc || _packets.Count >= packet.UnitsSrc)
                {
                    int validPackets = _packets.Take(packet.UnitsSrc).Count(p => p != null && p.Length > 0);
                    if (validPackets >= packet.UnitsSrc - 1)
                        shouldAssemble = true;
                }
            }
            else
            {
                // 视频流：只有在没有乱序时才组装；并引入“提前刷新”策略（flush_possible）
                if (!_frameBadOrder)
                {
                    if (packet.IsLastSrc)
                        shouldAssemble = true;
                    else if (IsFlushPossible(packet))
                        shouldAssemble = true;
                }
            }

            if (shouldAssemble)
            {
                // 音频流：即使有乱序也组装
                // 视频流：只有在没有乱序时才组装
                if (_type == TYPE_AUDIO || !_frameBadOrder)
                {
                    AssembleFrame(packet);
                }
            }
            else if (_type == TYPE_VIDEO && _frameBadOrder && packet.IsLastSrc && packet.UnitsFec == 0)
            {
                if (_packetLossSoftThresholdReached(packet))
                    TriggerFallback(packet, "missing source units with no FEC available");
            }
        }

        /// <summary>
        /// 提前刷新判断（flush_possible）
        /// 参考 chiaki-ng：当收到的源单元数已满足期望（或仅缺少 <=1 个）时可提前刷新
        /// 仅用于视频，且当前帧未标记乱序
        /// </summary>
        private bool IsFlushPossible(AVPacket packet)
        {
            if (_type != TYPE_VIDEO)
                return false;
            if (_frameBadOrder)
                return false;
            if (_packets.Count < packet.UnitsSrc)
                return false;

            // 统计前 UnitsSrc 个源单元的有效包数量（非空）
            int validPackets = 0;
            int limit = Math.Min(packet.UnitsSrc, _packets.Count);
            for (int i = 0; i < limit; i++)
            {
                var p = _packets[i];
                if (p != null && p.Length > 0)
                    validPackets++;
            }

            // 允许最多缺少 1 个源单元即提前刷新（与音频同口径、但仅在未乱序时启用）
            return validPackets >= packet.UnitsSrc - 1;
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
            // ✅ 检查帧超时（仅适用于视频流，参考 chiaki-ng）
            // 音频流不应该有超时检测，因为音频帧小且处理快，丢包会导致爆音
            if (_type == TYPE_VIDEO && _frameStartTime != DateTime.MinValue)
            {
                var elapsed = (DateTime.UtcNow - _frameStartTime).TotalMilliseconds;
                if (elapsed > FRAME_TIMEOUT_MS)
                {
                    _logger.LogWarning("⚠️ 帧 {Frame} 在 FEC 处理时超时 ({Elapsed}ms > {Timeout}ms)，触发 fallback", 
                        packet.FrameIndex, elapsed, FRAME_TIMEOUT_MS);
                    _frameTimeoutDropped++;
                    TriggerFallback(packet, $"frame timeout during FEC ({elapsed:F0}ms)");
                    return;
                }
            }

            // ✅ 音频启动期：忽略 FEC 路径，避免重复包引入的爆音（对齐 chiaki-ng 的启动处理）
            if (_type == TYPE_AUDIO && _audioStartup)
                return;

            if (!_frameBadOrder && _missing.Count == 0)
            {
                // 未乱序且不缺失，不需要 FEC；但如果已满足 flush_possible，也可直接刷新
                if (_type == TYPE_VIDEO && IsFlushPossible(packet))
                {
                    AssembleFrame(packet);
                }
                return;
            }
            if (!packet.IsLast) return;

            if (_missing.Count > packet.UnitsFec)
            {
                _fecAttempts++;
                _fecFailures++;
                _logger.LogWarning("⚠️ FEC insufficient: missing={Missing}, fec={Fec}", _missing.Count, packet.UnitsFec);
                // 细化结果：FEC 失败
                _frameResultCallback?.Invoke(new FrameProcessInfo(packet.FrameIndex, FrameProcessStatus.FecFailed, false, false, $"FEC insufficient: missing={_missing.Count}, fec={packet.UnitsFec}"));
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
                // 细化结果：FEC 失败
                _frameResultCallback?.Invoke(new FrameProcessInfo(packet.FrameIndex, FrameProcessStatus.FecFailed, false, false, "FEC recovery failed"));
                TriggerFallback(packet, "FEC recovery failed");
            }
        }

        private void AssembleFrame(AVPacket packet, bool recoveredByFec = false)
        {
            if (_currentFrameAssembled)
                return;
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

                // ✅ 检查 P 帧参考帧（参考 chiaki-ng 的 chiaki_video_receiver_flush_frame）
                bool frameRecovered = recoveredByFec;
                bool pFrameFallback = false;
                bool hasAlternativeRef = false;

                try
                {
                    pFrameFallback = CheckPFrameReferenceFrame(composedFrame, packet.FrameIndex, out hasAlternativeRef);
                }
                catch (Exception ex)
                {
                    // 如果 P 帧检查失败，记录日志但继续处理（不影响音频）
                    _logger.LogWarning(ex, "⚠️ P 帧参考帧检查失败，继续处理帧 {Frame}", packet.FrameIndex);
                }

                if (pFrameFallback && !hasAlternativeRef)
                {
                    // 缺少参考帧且找不到替代，触发 fallback
                    _logger.LogWarning("⚠️ P 帧 {Frame} 缺少参考帧且无替代，触发 fallback", packet.FrameIndex);
                    TriggerFallback(packet, "missing reference frame for P-frame");
                    return;
                }
                else if (pFrameFallback && hasAlternativeRef)
                {
                    // 尝试修改 bitstream 的参考帧（受控开关，失败则退回容错）
                    bool rewriteEnabled = true; // 预留：后续可改为配置或运行时开关
                    if (rewriteEnabled && _bitstreamParser != null)
                    {
                        try
                        {
                            if (_bitstreamParser.SetReferenceFrame(composedFrame, 0, out var modified))
                            {
                                composedFrame = modified;
                                _logger.LogInformation("🧩 P 帧 {Frame} 参考帧已重写并提交解码", packet.FrameIndex);
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ P 帧 {Frame} 参考帧重写未生效，继续使用原始帧（依赖解码器容错）", packet.FrameIndex);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ P 帧 {Frame} 参考帧重写失败，继续使用原始帧", packet.FrameIndex);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ P 帧 {Frame} 缺少原始参考帧，但找到替代参考帧（依赖解码器容错）", packet.FrameIndex);
                    }
                    frameRecovered = true; // 标记为恢复
                }

                _lastGoodVideoFrame = composedFrame;
                _callbackDone(composedFrame);

                // ✅ 记录流统计（参考 chiaki-ng: chiaki_stream_stats_frame）
                _streamStats.RecordFrame((ulong)composedFrame.Length);

                // ✅ 添加参考帧（参考 chiaki-ng 的 add_ref_frame）
                try
                {
                    _referenceFrameManager?.AddReferenceFrame(packet.FrameIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ 添加参考帧失败，帧 {Frame}", packet.FrameIndex);
                }

                var status = frameRecovered ? FrameProcessStatus.FecSuccess : FrameProcessStatus.Success;
                _frameResultCallback?.Invoke(new FrameProcessInfo(packet.FrameIndex, status, frameRecovered, false, null));
            }
            else
            {
                // ✅ 音频处理：直接回调，不受 P 帧检查影响
                _callbackDone(frameData);

                // ✅ 记录流统计（参考 chiaki-ng: chiaki_stream_stats_frame）
                _streamStats.RecordFrame((ulong)frameData.Length);
            }

            _lastComplete = packet.FrameIndex;
            _frameIndexPrev = packet.FrameIndex; // ✅ 记录至少部分解码成功的上一帧
            _currentFrameAssembled = true;
            _frameStartTime = DateTime.MinValue; // ✅ 重置帧开始时间，准备处理下一个帧

            // ✅ 音频启动期：累计成功帧，达阈值后退出启动期，恢复正常 FEC 行为
            if (_type == TYPE_AUDIO && _audioStartup)
            {
                _audioStartupSuccessFrames++;
                if (_audioStartupSuccessFrames >= AUDIO_STARTUP_SUCCESS_THRESHOLD)
                {
                    _audioStartup = false;
                    _logger.LogDebug("🔊 Audio startup completed after {Count} frames", _audioStartupSuccessFrames);
                }
            }
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

        public (int received, int lost, int timeoutDropped) ConsumeAndResetCounters()
        {
            lock (_lock)
            {
                int received = _received;
                int lost = _lost;
                int timeoutDropped = _frameTimeoutDropped;
                _received = 0;
                _lost = 0;
                _frameTimeoutDropped = 0;
                return (received, lost, timeoutDropped);
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

        /// <summary>
        /// 获取并重置帧索引统计（frame_index_prev / frames_lost）
        /// </summary>
        public (int frameIndexPrev, int framesLost) ConsumeAndResetFrameIndexStats()
        {
            lock (_lock)
            {
                int prev = _frameIndexPrev;
                int lost = _framesLost;
                _framesLost = 0;
                return (prev, lost);
            }
        }

        /// <summary>
        /// 获取流统计信息（参考 chiaki-ng 的 ChiakiStreamStats）
        /// </summary>
        public StreamStats GetStreamStats()
        {
            return _streamStats;
        }

        /// <summary>
        /// 获取并重置流统计信息（参考 chiaki-ng: chiaki_stream_stats_reset）
        /// </summary>
        public (ulong frames, ulong bytes) GetAndResetStreamStats()
        {
            return _streamStats.GetAndReset();
        }

        /// <summary>
        /// 检查 P 帧的参考帧（参考 chiaki-ng 的 chiaki_video_receiver_flush_frame）
        /// 返回 (是否缺少参考帧, 是否找到替代参考帧)
        /// </summary>
        private bool CheckPFrameReferenceFrame(byte[] composedFrame, int frameIndex, out bool hasAlternativeRef)
        {
            hasAlternativeRef = false;

            if (_type != TYPE_VIDEO || _referenceFrameManager == null)
                return false;

            // 延迟初始化 BitstreamParser（需要知道 codec）
            if (_bitstreamParser == null)
            {
                // 从 header 检测 codec
                _detectedCodec = DetectCodecFromHeader(Header);
                if (_detectedCodec != null)
                {
                    _bitstreamParser = new BitstreamParser(_detectedCodec, null); // Logger 可选
                }
                else
                {
                    // 默认使用 h264
                    _bitstreamParser = new BitstreamParser("h264", null); // Logger 可选
                }
            }

            // 解析 slice header
            if (!_bitstreamParser.ParseSlice(composedFrame, out var slice))
                return false;

            // 只处理 P 帧
            if (slice.SliceType != SliceType.P)
                return false;

            // 检查参考帧
            if (slice.ReferenceFrame == 0xFF)
            {
                // I 帧或无效，不需要参考帧
                return false;
            }

            // 计算参考帧索引（参考 chiaki-ng）
            int refFrameIndex = frameIndex - (int)slice.ReferenceFrame - 1;

            // 检查参考帧是否存在
            if (_referenceFrameManager.HasReferenceFrame(refFrameIndex))
            {
                // 参考帧存在，正常
                return false;
            }

            // 参考帧不存在，尝试查找替代参考帧（参考 chiaki-ng）
            int alternativeRefFrame = _referenceFrameManager.FindAvailableReferenceFrame(frameIndex, slice.ReferenceFrame);
            if (alternativeRefFrame >= 0)
            {
                hasAlternativeRef = true;
                _logger.LogWarning("⚠️ P 帧 {Frame} 缺少参考帧 {RefFrame}，找到替代参考帧 {AltRefFrame}",
                    frameIndex, refFrameIndex, frameIndex - alternativeRefFrame - 1);
                // 注意：由于 bitstream 修改复杂，当前不修改 bitstream
                // 依赖解码器的容错能力
            }
            else
            {
                _logger.LogWarning("⚠️ P 帧 {Frame} 缺少参考帧 {RefFrame}，且无替代参考帧",
                    frameIndex, refFrameIndex);
            }

            return true; // 缺少参考帧
        }

        /// <summary>
        /// 从 header 检测 codec
        /// </summary>
        private string? DetectCodecFromHeader(byte[] header)
        {
            if (header == null || header.Length < 10)
                return null;

            // 查找 NAL unit
            for (int i = 0; i < header.Length - 4; i++)
            {
                if (header[i] == 0x00 && header[i + 1] == 0x00)
                {
                    int offset = 0;
                    if (header[i + 2] == 0x01)
                        offset = 3;
                    else if (header[i + 2] == 0x00 && header[i + 3] == 0x01)
                        offset = 4;
                    else
                        continue;

                    if (i + offset >= header.Length)
                        continue;

                    byte nal = header[i + offset];
                    
                    // H.265/HEVC: NAL type 在低 6 位
                    if ((nal & 0x7E) == 0x40 || (nal & 0x7E) == 0x42 || (nal & 0x7E) == 0x44)
                        return "hevc";
                    
                    // H.264: NAL type 在低 5 位
                    if ((nal & 0x1F) is 5 or 7 or 8)
                        return "h264";
                }
            }

            return null;
        }

        /// <summary>
        /// 更新 header（用于 profile 切换时）
        /// </summary>
        public void UpdateHeader(byte[] newHeader)
        {
            lock (_lock)
            {
                if (_type == TYPE_VIDEO)
                {
                    Header = newHeader;
                    _logger.LogDebug("AVStream header 已更新，长度={Length}", newHeader.Length);
                }
            }
        }

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
