using RemotePlay.Services.Streaming.AV.Bitstream;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils;
using System;
using System.Collections.Generic;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 视频接收器
    /// 负责处理视频流，包括 profile 切换、帧索引跟踪、参考帧管理等
    /// </summary>
    public class VideoReceiver
    {
        private readonly ILogger<VideoReceiver>? _logger;
        private readonly FrameProcessor _frameProcessor;
        private readonly ReferenceFrameManager _referenceFrameManager;
        private BitstreamParser? _bitstreamParser; // 延迟初始化，需要知道 codec 类型

        private VideoProfile[] _profiles = Array.Empty<VideoProfile>();
        private int _profileCur = -1;
        private string? _detectedCodec; // 检测到的 codec 类型

        private int _frameIndexCur = -1;
        private int _frameIndexPrev = -1;
        private int _frameIndexPrevComplete = 0;
        private int _framesLost = 0;

        private Action<int, int>? _corruptFrameCallback;
        private Action? _requestKeyframeCallback;

        // ✅ 参考链断裂检测：当P帧缺少参考帧时，标记为断裂并丢弃后续P/B帧直到下一个IDR
        // ⚠️ 放宽策略：允许尝试解码，减少冻结时间
        private bool _referenceChainBroken = false; // 参考链是否断裂
        private int _lastValidFrameIndex = -1; // 最后一个有效帧的索引
        private DateTime _referenceChainBrokenTime = DateTime.MinValue; // 参考链断裂的时间戳
        private const int REFERENCE_CHAIN_TIMEOUT_MS = 300; // ✅ 缩短超时时间到300ms，更快恢复
        
        // ✅ 修复问题2：使用独立的计数器，避免逻辑冲突
        private int _consecutiveDroppedFrames = 0; // 连续被丢弃的帧数（真正丢弃时才增加）
        private int _consecutiveBypassAttempts = 0; // 连续允许解码的尝试次数（允许解码时增加）
        private const int MAX_CONSECUTIVE_DROPPED = 2; // 最多连续丢弃2帧，之后强制尝试解码
        private const int MAX_CONSECUTIVE_BYPASS = 5; // 最多连续尝试5次，之后标记为断裂
        
        private DateTime _lastFrameFailureTime = DateTime.MinValue; // 最后一次帧失败的时间
        private const int FRAME_FAILURE_GRACE_PERIOD_MS = 500; // 帧失败后的宽限期（500ms内允许尝试解码）

        private readonly object _lock = new();
        
        // ✅ 修复问题4：统一入口方法，避免多处写入_lastFrameFailureTime
        /// <summary>
        /// 通知帧失败，进入宽限期
        /// </summary>
        private void NotifyFrameFailure()
        {
            var now = DateTime.UtcNow;
            // ✅ 避免频繁更新，如果已经在宽限期内，不重复更新（除非超过冷却时间）
            if (_lastFrameFailureTime != DateTime.MinValue)
            {
                var elapsed = (now - _lastFrameFailureTime).TotalMilliseconds;
                if (elapsed < FRAME_FAILURE_GRACE_PERIOD_MS * 0.8) // 80%宽限期内不重复更新
                {
                    return;
                }
            }
            _lastFrameFailureTime = now;
        }
        
        // ✅ 修复问题1：封装P帧缺参考帧的处理逻辑
        /// <summary>
        /// 处理P帧缺少参考帧的情况
        /// </summary>
        /// <returns>是否应该尝试解码（true=允许解码，false=应该丢弃）</returns>
        private bool HandleMissingReferenceForPFrame(int frameIndex, int refFrameIndex, ref byte[]? frame, ref bool recovered)
        {
            // 浏览器（WebRTC）解码器遇到缺参考帧的 P 帧会报错并停止解码直到下一个关键帧，
            // 因此只允许"改写到可用参考帧"后发送，否则丢弃（与 chiaki-ng 一致）
            // ✅ 先尝试查找替代参考帧
            int alternativeRefFrame = _referenceFrameManager.FindAvailableReferenceFrame(frameIndex, (uint)(frameIndex - refFrameIndex - 1));
            if (alternativeRefFrame >= 0 && _bitstreamParser != null)
            {
                // 尝试修改 bitstream
                if (_bitstreamParser.SetReferenceFrame(frame!, (uint)alternativeRefFrame, out byte[]? modified))
                {
                    frame = modified;
                    recovered = true;
                    _logger?.LogWarning("✅ 参考链修复：P帧 {Frame} 使用替代参考帧 {AltRefFrame}",
                        frameIndex, frameIndex - alternativeRefFrame - 1);
                    return true; // 允许解码
                }
                else
                {
                    _logger?.LogWarning("⚠️ P帧 {Frame} 缺少参考帧 {RefFrame}，找到替代但无法修改bitstream",
                        frameIndex, refFrameIndex);
                }
            }

            if (!_referenceChainBroken)
            {
                _referenceChainBroken = true;
                _referenceChainBrokenTime = DateTime.UtcNow;
                _logger?.LogWarning("⚠️ P帧 {Frame} 缺少参考帧 {RefFrame}，丢弃并请求关键帧", frameIndex, refFrameIndex);
            }

            _requestKeyframeCallback?.Invoke();
            return false;
        }

        public VideoReceiver(ILogger<VideoReceiver>? logger = null)
        {
            _logger = logger;
            _frameProcessor = new FrameProcessor(null); // FrameProcessor2 使用 ILogger<FrameProcessor2>?
            _referenceFrameManager = new ReferenceFrameManager(null); // ReferenceFrameManager 使用 ILogger<ReferenceFrameManager>?
            // BitstreamParser 延迟初始化，需要知道 codec 类型
        }

        /// <summary>
        /// 设置请求关键帧回调
        /// </summary>
        public void SetRequestKeyframeCallback(Action? callback)
        {
            lock (_lock)
            {
                _requestKeyframeCallback = callback;
            }
        }

        /// <summary>
        /// 设置 corrupt frame 回调
        /// </summary>
        public void SetCorruptFrameCallback(Action<int, int>? callback)
        {
            lock (_lock)
            {
                _corruptFrameCallback = callback;
            }
        }

        /// <summary>
        /// 设置包统计（用于拥塞控制）
        /// </summary>
        public void SetPacketStats(Congestion.PacketStats? packetStats)
        {
            _frameProcessor?.SetPacketStats(packetStats);
        }

        /// <summary>
        /// 设置视频 profiles
        /// </summary>
        public void SetStreamInfo(VideoProfile[] profiles)
        {
            lock (_lock)
            {
                // ✅ 如果 profiles 已经设置过，检查是否需要更新
                if (_profiles.Length > 0)
                {
                    // 如果 profiles 数组相同（引用相同或内容相同），则忽略
                    if (profiles != null && profiles.Length == _profiles.Length)
                    {
                        bool isSame = true;
                        for (int i = 0; i < profiles.Length; i++)
                        {
                            if (profiles[i] != _profiles[i])
                            {
                                isSame = false;
                                break;
                            }
                        }
                        if (isSame)
                        {
                            _logger?.LogDebug("Video Receiver profiles already set (same profiles), skipping");
                            return;
                        }
                    }
                    
                    // 如果 profiles 不同，允许更新（用于 profile 切换场景）
                    _logger?.LogInformation("Video Receiver profiles updating (was {OldCount}, now {NewCount})", 
                        _profiles.Length, profiles?.Length ?? 0);
                }

                _profiles = profiles ?? Array.Empty<VideoProfile>();
                _logger?.LogInformation("Video Profiles: {Count}", _profiles.Length);
                for (int i = 0; i < _profiles.Length; i++)
                {
                    _logger?.LogInformation("  {Index}: {Width}x{Height}", i, _profiles[i].Width, _profiles[i].Height);
                }

                // ✅ 检测 codec 类型并初始化 BitstreamParser
                if (_profiles.Length > 0 && _profiles[0].Header != null && _profiles[0].Header.Length > 0)
                {
                    DetectCodecFromHeader(_profiles[0].Header);
                    if (_detectedCodec != null)
                    {
                        _bitstreamParser = new BitstreamParser(_detectedCodec, null);
                        // 解析 SPS 获取关键参数
                        if (!_bitstreamParser.ParseHeader(_profiles[0].Header))
                        {
                            _logger?.LogWarning("Failed to parse video header for bitstream");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 从 header 检测 codec 类型
        /// </summary>
        private void DetectCodecFromHeader(byte[] header)
        {
            if (header == null || header.Length < 10)
                return;

            // 查找 startcode
            int offset = -1;
            for (int i = 0; i < header.Length - 3; i++)
            {
                if (header[i] == 0x00 && header[i + 1] == 0x00)
                {
                    if (header[i + 2] == 0x01)
                    {
                        offset = i + 3;
                        break;
                    }
                    if (i + 3 < header.Length && header[i + 2] == 0x00 && header[i + 3] == 0x01)
                    {
                        offset = i + 4;
                        break;
                    }
                }
            }

            if (offset < 0 || offset >= header.Length)
                return;

            // 检查 H.265 HEVC
            byte nalType = (byte)((header[offset] >> 1) & 0x3F);
            if (nalType == 33) // SPS
            {
                _detectedCodec = "hevc";
                return;
            }

            // 检查 H.264
            nalType = (byte)(header[offset] & 0x1F);
            if (nalType == 7) // SPS
            {
                _detectedCodec = "h264";
                return;
            }
        }

        /// <summary>
        /// 处理 AV 包
        /// ✅ 优化：整个方法在锁内，确保线程安全，但回调在锁外调用以避免死锁和阻塞
        /// </summary>
        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[], bool, bool>? onFrameReady)
        {
            // ✅ 优化：在锁外准备回调参数，避免在锁内调用外部回调
            List<(byte[] data, bool recovered, bool success)> pendingCallbacks = new();
            Action? pendingKeyframeRequest = null;
            (int start, int end)? pendingCorruptFrame = null;
            
            lock (_lock)
            {
                // 检查旧帧
                if (_frameIndexCur >= 0 && IsSeq16Older(packet.FrameIndex, _frameIndexCur))
                {
                    _logger?.LogWarning("Video Receiver received old frame packet: {Frame} < {Current}", 
                        packet.FrameIndex, _frameIndexCur);
                    return;
                }

                // 检查 adaptive stream index（profile 切换）
                if (_profileCur < 0 || _profileCur != packet.AdaptiveStreamIndex)
                {
                    if (packet.AdaptiveStreamIndex >= _profiles.Length)
                    {
                        _logger?.LogError("Packet has invalid adaptive stream index {Index} >= {Count}",
                            packet.AdaptiveStreamIndex, _profiles.Length);
                        return;
                    }

                    var oldProfile = _profileCur >= 0 ? _profiles[_profileCur] : null;
                    _profileCur = packet.AdaptiveStreamIndex;
                    var newProfile = _profiles[_profileCur];
                    _logger?.LogInformation("Switched to profile {Index}, resolution: {Width}x{Height}", 
                        _profileCur, newProfile.Width, newProfile.Height);

                    // ✅ 检测新 profile 的 codec 并更新 BitstreamParser
                    if (newProfile.Header != null && newProfile.Header.Length > 0)
                    {
                        DetectCodecFromHeader(newProfile.Header);
                        if (_detectedCodec != null)
                        {
                            _bitstreamParser = new BitstreamParser(_detectedCodec, null);
                            // 解析新 profile 的 SPS
                            if (!_bitstreamParser.ParseHeader(newProfile.Header))
                            {
                                _logger?.LogWarning("Failed to parse video header for bitstream");
                            }
                        }
                    }

                    // ✅ 优化：在锁外调用回调，避免阻塞
                    pendingCallbacks.Add((newProfile.HeaderWithPadding, false, false));
                }

                // 检测新帧
                if (_frameIndexCur < 0 || (!IsSeq16Older(packet.FrameIndex, _frameIndexCur) && packet.FrameIndex != _frameIndexCur))
                {
                    // 如果上一帧还没有刷新，先刷新它（在刷新后报告统计，确保统计准确）
                    if (_frameIndexCur >= 0 && _frameIndexPrev != _frameIndexCur)
                    {
                        // ✅ 优化：FlushFrame现在收集回调，不在锁内调用
                        FlushFrameInternal(pendingCallbacks, ref pendingCorruptFrame);
                        // 在刷新后报告上一帧的统计信息（确保统计完整准确）
                        _frameProcessor.ReportPacketStats();
                    }

                    // 检测帧丢失
                    ushort nextFrameExpected = (ushort)(_frameIndexPrevComplete + 1);
                    if (!IsSeq16Older(packet.FrameIndex, nextFrameExpected) && packet.FrameIndex != nextFrameExpected &&
                        !(packet.FrameIndex == 1 && _frameIndexCur < 0))
                    {
                        int start = nextFrameExpected;
                        int end = (ushort)(packet.FrameIndex - 1);
                        int lostCount = end - start + 1;
                        
                        _logger?.LogWarning("Detected missing or corrupt frame(s) from {From} to {To} (丢失 {Count} 帧)", 
                            start, end, lostCount);
                        
                        // ✅ 关键修复：丢失帧时，记录失败时间并清除参考链断裂标记
                        // 在宽限期内，允许后续帧即使缺少参考帧也尝试解码
                        if (lostCount > 0)
                        {
                            NotifyFrameFailure(); // ✅ 使用统一入口
                            
                            if (_referenceChainBroken)
                            {
                                _referenceChainBroken = false;
                                _consecutiveDroppedFrames = 0;
                                _consecutiveBypassAttempts = 0;
                                _referenceChainBrokenTime = DateTime.MinValue;
                            }
                            
                            // ✅ 如果丢失帧数过多，重置参考链和解码器状态
                            // ✅ 优化：提高阈值到 20 帧，避免过度重置导致视频冻结
                            if (lostCount > 20)
                            {
                                _logger?.LogWarning("⚠️ 大量帧丢失（{Count} 帧），重置参考链和解码器状态", lostCount);
                                _referenceFrameManager.Reset();
                                _frameProcessor.Reset();
                            }
                            
                            // ✅ 优化：在锁外调用回调，避免阻塞
                            pendingKeyframeRequest = _requestKeyframeCallback;
                        }
                        
                        // ✅ 优化：在锁外调用回调，避免阻塞
                        pendingCorruptFrame = (start, end);
                    }

                    _frameIndexCur = packet.FrameIndex;
                    
                    // 创建用于 AllocFrame 的包副本
                    var allocPacket = CreatePacketCopy(packet, decryptedData);
                    if (!_frameProcessor.AllocFrame(allocPacket))
                    {
                        _logger?.LogWarning("Video receiver could not allocate frame for packet: frame={Frame}, " +
                            "unitIndex={UnitIndex}/{Total}, frameIndexCur={FrameCur}",
                            packet.FrameIndex, packet.UnitIndex, packet.UnitsTotal, _frameIndexCur);
                        
                        // ✅ 关键修复：如果 AllocFrame 失败，可能需要重置 FrameProcessor
                        // 这通常发生在帧结构发生变化时（如分辨率切换）
                        _logger?.LogWarning("⚠️ AllocFrame 失败，可能需要重置 FrameProcessor");
                    }
                }

                // 添加 unit 到帧处理器
                var unitPacket = CreatePacketCopy(packet, decryptedData);
                bool putUnitSuccess = _frameProcessor.PutUnit(unitPacket);
                if (!putUnitSuccess)
                {
                    _logger?.LogWarning("Video receiver could not put unit: frame={Frame}, unitIndex={UnitIndex}/{Total}, " +
                        "frameIndexCur={FrameCur}, frameIndexPrev={FramePrev}",
                        packet.FrameIndex, packet.UnitIndex, packet.UnitsTotal, _frameIndexCur, _frameIndexPrev);
                    
                    // ✅ 关键修复：如果 PutUnit 失败但这是最后一个 unit，仍然尝试刷新帧
                    // 因为帧可能已经可以刷新了（通过 FEC 或其他 unit）
                    if (packet.UnitIndex == packet.UnitsTotal - 1)
                    {
                        _logger?.LogWarning("⚠️ PutUnit 失败但这是最后一个 unit，尝试强制刷新帧 {Frame}", packet.FrameIndex);
                    }
                }

                // ✅ 关键修复：如果可以刷新，立即刷新（即使 PutUnit 失败，如果是最后一个 unit 也要刷新）
                if (_frameIndexCur != _frameIndexPrev)
                {
                    bool shouldFlush = _frameProcessor.FlushPossible() || 
                                     (packet.UnitIndex == packet.UnitsTotal - 1);
                    
                    if (shouldFlush)
                    {
                        // ✅ 优化：FlushFrame现在收集回调，不在锁内调用
                        FlushFrameInternal(pendingCallbacks, ref pendingCorruptFrame);
                        // 在刷新后报告帧的统计信息（确保统计完整准确）
                        _frameProcessor.ReportPacketStats();
                    }
                }
            }
            
            // ✅ 优化：在锁外调用所有回调，避免死锁和阻塞
            foreach (var (data, recovered, success) in pendingCallbacks)
            {
                onFrameReady?.Invoke(data, recovered, success);
            }
            
            if (pendingKeyframeRequest != null)
            {
                pendingKeyframeRequest.Invoke();
            }
            
            if (pendingCorruptFrame.HasValue)
            {
                _corruptFrameCallback?.Invoke(pendingCorruptFrame.Value.start, pendingCorruptFrame.Value.end);
            }
        }

        /// <summary>
        /// 刷新帧（内部方法，在锁内调用，收集回调到列表）
        /// </summary>
        private void FlushFrameInternal(List<(byte[] data, bool recovered, bool success)> pendingCallbacks, ref (int start, int end)? pendingCorruptFrame)
        {
            FlushResult flushResult = _frameProcessor.Flush(out byte[]? frame, out int frameSize);

            if (flushResult == FlushResult.Failed || flushResult == FlushResult.FecFailed)
            {
                if (flushResult == FlushResult.FecFailed)
                {
                    ushort nextFrameExpected = (ushort)(_frameIndexPrevComplete + 1);
                    // ✅ Bug 1 修复：收集corrupt frame通知，在锁外调用
                    pendingCorruptFrame = (nextFrameExpected, _frameIndexCur);
                    _framesLost += _frameIndexCur - nextFrameExpected + 1;
                }
                
                // ✅ 关键修复：即使帧失败，也要更新索引，避免后续帧检测到大量丢失
                _frameIndexPrev = _frameIndexCur;
                // ⚠️ 注意：不更新 _frameIndexPrevComplete，因为帧未完成
                
                // ✅ 修复问题3：flush失败时，从ReferenceFrameManager中移除损坏的帧
                _referenceFrameManager.RemoveReferenceFrame(_frameIndexCur);
                
                // ✅ 关键修复：帧失败时，记录失败时间并清除参考链断裂标记
                // 在宽限期内，允许后续帧即使缺少参考帧也尝试解码
                NotifyFrameFailure(); // ✅ 使用统一入口
                
                _logger?.LogWarning("Failed to complete frame {Frame}（包缺失且 FEC 无法恢复，已通知主机）", _frameIndexCur);
                if (_referenceChainBroken)
                {
                    _referenceChainBroken = false;
                    _consecutiveDroppedFrames = 0;
                    _consecutiveBypassAttempts = 0;
                    _referenceChainBrokenTime = DateTime.MinValue;
                }
                
                // ✅ 优化：关键帧请求已经在ProcessPacket中收集，这里不需要处理
                return;
            }

            bool success = flushResult != FlushResult.FecFailed;
            bool recovered = flushResult == FlushResult.FecSuccess;

            // ✅ 检查参考链是否断裂：如果之前标记为断裂，且当前帧不是IDR，则丢弃
            bool isIdrFrame = false;
            BitstreamSlice? slice = null;
            if (frame != null && frameSize > 0 && _bitstreamParser != null)
            {
                BitstreamSlice parsedSlice;
                if (_bitstreamParser.ParseSlice(frame, out parsedSlice))
                {
                    slice = parsedSlice;
                    
                    // ✅ 检测是否为IDR帧（使用IsIdr属性）
                    isIdrFrame = parsedSlice.IsIdr;
                    
                    // 只按参考帧是否可用决定 P 帧能否发送；参考链断裂期间，主机收到损坏帧通知后
                    // 会让后续帧改为参考丢失前的好帧，这些帧能通过检查，从而无需等待 IDR 即可恢复
                    if (parsedSlice.SliceType == SliceType.P)
                    {
                        int refFrameIndex = _frameIndexCur - (int)parsedSlice.ReferenceFrame - 1;
                        if (parsedSlice.ReferenceFrame != 0xFF && !_referenceFrameManager.HasReferenceFrame(refFrameIndex))
                        {
                            bool shouldDecode = HandleMissingReferenceForPFrame(_frameIndexCur, refFrameIndex, ref frame, ref recovered);
                            if (!shouldDecode)
                            {
                                // 不更新 _frameIndexPrevComplete：下一帧开始时会把这段报告为损坏帧
                                _framesLost++;
                                _frameIndexPrev = _frameIndexCur;
                                return;
                            }
                        }
                        else if (_referenceChainBroken)
                        {
                            _referenceChainBroken = false;
                            _referenceChainBrokenTime = DateTime.MinValue;
                            _logger?.LogInformation("✅ 参考链恢复：P帧 {Frame} 的参考帧可用", _frameIndexCur);
                        }
                    }
                }
            }
            
            // ✅ 如果收到IDR帧，清除参考链断裂标记并重置参考帧管理器
            if (isIdrFrame)
            {
                if (_referenceChainBroken)
                {
                    var recoveryTime = _referenceChainBrokenTime != DateTime.MinValue 
                        ? (DateTime.UtcNow - _referenceChainBrokenTime).TotalMilliseconds 
                        : 0;
                    _referenceChainBroken = false;
                    _referenceChainBrokenTime = DateTime.MinValue; // 清除时间戳
                    _consecutiveDroppedFrames = 0; // 重置连续丢弃计数
                    _consecutiveBypassAttempts = 0; // 重置连续尝试计数
                    _lastValidFrameIndex = _frameIndexCur;
                    _logger?.LogInformation("✅ 参考链恢复：收到IDR帧 {Frame}，恢复正常解码（恢复耗时：{RecoveryTime}ms）", 
                        _frameIndexCur, (int)recoveryTime);
                }
                
                // ✅ IDR帧到来时，重置参考帧管理器（开始新的GOP）
                _referenceFrameManager.Reset();
            }

            // 解码器（尤其是浏览器）收到无法解码的帧会停止输出直到下一个关键帧，因此只发送完整且参考可用的帧
            if (success && frame != null && frameSize > 0)
            {
                // 组合 header + frame
                byte[] composedFrame;
                if (_profileCur >= 0 && _profileCur < _profiles.Length)
                {
                    var profile = _profiles[_profileCur];
                    composedFrame = new byte[profile.HeaderWithPadding.Length + frameSize];
                    Array.Copy(profile.HeaderWithPadding, 0, composedFrame, 0, profile.HeaderWithPadding.Length);
                    Array.Copy(frame, 0, composedFrame, profile.HeaderWithPadding.Length, frameSize);
                }
                else
                {
                    composedFrame = new byte[frameSize];
                    Array.Copy(frame, 0, composedFrame, 0, frameSize);
                }

                _framesLost = 0;
                _referenceFrameManager.AddReferenceFrame(_frameIndexCur);
                _lastValidFrameIndex = _frameIndexCur;
                if (isIdrFrame)
                {
                    _referenceChainBroken = false;
                    _lastFrameFailureTime = DateTime.MinValue;
                }

                // 回调在锁外执行
                pendingCallbacks.Add((composedFrame, recovered, true));
            }
            _frameIndexPrev = _frameIndexCur;

            if (success)
                _frameIndexPrevComplete = _frameIndexCur;
        }

        private static bool IsSeq16Older(int seq, int cur)
        {
            int diff = (seq - cur) & 0xFFFF;
            return diff > 0x8000;
        }

        /// <summary>
        /// 创建 AVPacket 的副本（用于 FrameProcessor）
        /// 由于 AVPacket 的属性是 private set，我们需要通过反射或创建一个包装类
        /// 这里我们创建一个简单的包装类
        /// </summary>
        private static AVPacketWrapper CreatePacketCopy(AVPacket original, byte[] decryptedData)
        {
            return new AVPacketWrapper
            {
                Type = original.Type,
                FrameIndex = original.FrameIndex,
                UnitIndex = original.UnitIndex,
                UnitsTotal = original.UnitsTotal,
                UnitsSrc = original.UnitsSrc,
                UnitsFec = original.UnitsFec,
                Data = decryptedData
            };
        }

        public StreamStats2 GetStreamStats()
        {
            return _frameProcessor.GetStreamStats();
        }

        public (ulong frames, ulong bytes) GetAndResetStreamStats()
        {
            return _frameProcessor.GetAndResetStreamStats();
        }

        /// <summary>
        /// 获取并重置 packet stats（用于拥塞控制）
        /// </summary>
        /// <summary>
        /// 获取并重置 packet stats（已过时）
        /// 注意：统计现在由 PacketStats 统一管理，请使用 PacketStats.GetAndReset
        /// </summary>
        [Obsolete("统计现在由 PacketStats 统一管理，请使用 PacketStats.GetAndReset")]
        public (ulong received, ulong lost) GetAndResetPacketStats()
        {
            // 返回空值，因为统计现在由 PacketStats 统一管理
            return (0, 0);
        }

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
    }

    /// <summary>
    /// AVPacket 的包装类，用于 FrameProcessor
    /// </summary>
    public class AVPacketWrapper
    {
        public HeaderType Type { get; set; }
        public ushort FrameIndex { get; set; }
        public int UnitIndex { get; set; }
        public int UnitsTotal { get; set; }
        public int UnitsSrc { get; set; }
        public int UnitsFec { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }
}

