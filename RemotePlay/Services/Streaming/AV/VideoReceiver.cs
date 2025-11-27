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
        private bool _referenceChainBroken = false; // 参考链是否断裂
        private int _lastValidFrameIndex = -1; // 最后一个有效帧的索引

        private readonly object _lock = new();

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
        /// </summary>
        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[], bool, bool>? onFrameReady)
        {
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

                    // 通知 profile 切换（发送新的 header）
                    onFrameReady?.Invoke(newProfile.HeaderWithPadding, false, false);
                }

                // 检测新帧
                if (_frameIndexCur < 0 || (!IsSeq16Older(packet.FrameIndex, _frameIndexCur) && packet.FrameIndex != _frameIndexCur))
                {
                    // 如果上一帧还没有刷新，先刷新它（在刷新后报告统计，确保统计准确）
                    if (_frameIndexCur >= 0 && _frameIndexPrev != _frameIndexCur)
                    {
                        FlushFrame(onFrameReady);
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
                        _logger?.LogWarning("Detected missing or corrupt frame(s) from {From} to {To}", 
                            start, end);
                        // 发送 corrupt frame 通知
                        _corruptFrameCallback?.Invoke(start, end);
                    }

                    _frameIndexCur = packet.FrameIndex;
                    
                    // 创建用于 AllocFrame 的包副本
                    var allocPacket = CreatePacketCopy(packet, decryptedData);
                    if (!_frameProcessor.AllocFrame(allocPacket))
                    {
                        _logger?.LogWarning("Video receiver could not allocate frame for packet");
                    }
                }

                // 添加 unit 到帧处理器
                var unitPacket = CreatePacketCopy(packet, decryptedData);
                if (!_frameProcessor.PutUnit(unitPacket))
                {
                    _logger?.LogWarning("Video receiver could not put unit");
                }

                // 如果可以刷新，立即刷新
                if (_frameIndexCur != _frameIndexPrev)
                {
                    if (_frameProcessor.FlushPossible() || packet.UnitIndex == packet.UnitsTotal - 1)
                    {
                        FlushFrame(onFrameReady);
                        // 在刷新后报告帧的统计信息（确保统计完整准确）
                        _frameProcessor.ReportPacketStats();
                    }
                }
            }
        }

        /// <summary>
        /// 刷新帧
        /// </summary>
        private void FlushFrame(Action<byte[], bool, bool>? onFrameReady)
        {
            FlushResult flushResult = _frameProcessor.Flush(out byte[]? frame, out int frameSize);

            if (flushResult == FlushResult.Failed || flushResult == FlushResult.FecFailed)
            {
                if (flushResult == FlushResult.FecFailed)
                {
                    ushort nextFrameExpected = (ushort)(_frameIndexPrevComplete + 1);
                    // 发送 corrupt frame 通知
                    _corruptFrameCallback?.Invoke(nextFrameExpected, _frameIndexCur);
                    _framesLost += _frameIndexCur - nextFrameExpected + 1;
                    _frameIndexPrev = _frameIndexCur;
                }
                _logger?.LogWarning("Failed to complete frame {Frame}", _frameIndexCur);
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
                    
                    // ✅ 如果参考链已断裂，且当前帧不是IDR，则丢弃
                    if (_referenceChainBroken && !isIdrFrame)
                    {
                        _logger?.LogWarning("🚫 参考链断裂：丢弃P/B帧 {Frame}（等待IDR恢复）", _frameIndexCur);
                        success = false;
                        _framesLost++;
                        return; // 直接返回，不处理此帧
                    }
                    
                    if (parsedSlice.SliceType == SliceType.P)
                    {
                        int refFrameIndex = _frameIndexCur - (int)parsedSlice.ReferenceFrame - 1;
                        if (parsedSlice.ReferenceFrame != 0xFF && !_referenceFrameManager.HasReferenceFrame(refFrameIndex))
                        {
                            // ✅ 检测到P帧缺少参考帧，标记参考链断裂
                            _referenceChainBroken = true;
                            _logger?.LogWarning("⚠️ 参考链断裂：P帧 {Frame} 缺少参考帧 {RefFrame}，将丢弃后续P/B帧直到下一个IDR",
                                _frameIndexCur, refFrameIndex);
                            
                            // ✅ A. 当参考链断裂时清除解码器状态（防止硬件解码器卡住）
                            _referenceFrameManager.Reset();
                            _frameProcessor.Reset();
                            _logger?.LogWarning("🔄 已清除解码器状态（参考链断裂）");
                            
                            // 立即请求关键帧
                            _requestKeyframeCallback?.Invoke();
                            
                            // 尝试查找替代参考帧
                            int alternativeRefFrame = _referenceFrameManager.FindAvailableReferenceFrame(_frameIndexCur, parsedSlice.ReferenceFrame);
                            if (alternativeRefFrame >= 0)
                            {
                                // 尝试修改 bitstream
                                if (_bitstreamParser.SetReferenceFrame(frame, (uint)alternativeRefFrame, out byte[]? modified))
                                {
                                    frame = modified;
                                    recovered = true;
                                    _referenceChainBroken = false; // 恢复成功，清除断裂标记
                                    _logger?.LogWarning("✅ 参考链恢复：P帧 {Frame} 使用替代参考帧 {AltRefFrame}",
                                        _frameIndexCur, _frameIndexCur - alternativeRefFrame - 1);
                                }
                                else
                                {
                                    _logger?.LogWarning("Missing reference frame {RefFrame} for decoding frame {Frame}, found alternative but could not modify bitstream",
                                        refFrameIndex, _frameIndexCur);
                                }
                            }
                            else
                            {
                                success = false;
                                _framesLost++;
                                _logger?.LogWarning("Missing reference frame {RefFrame} for decoding frame {Frame}",
                                    refFrameIndex, _frameIndexCur);
                            }
                        }
                    }
                }
            }
            
            // ✅ 如果收到IDR帧，清除参考链断裂标记并重置参考帧管理器
            if (isIdrFrame)
            {
                if (_referenceChainBroken)
                {
                    _referenceChainBroken = false;
                    _lastValidFrameIndex = _frameIndexCur;
                    _logger?.LogInformation("✅ 参考链恢复：收到IDR帧 {Frame}，恢复正常解码", _frameIndexCur);
                }
                
                // ✅ IDR帧到来时，重置参考帧管理器（开始新的GOP）
                _referenceFrameManager.Reset();
            }

            if (success && onFrameReady != null && frame != null)
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

                bool frameProcessed = true; // 假设回调成功
                if (frameProcessed)
                {
                    _framesLost = 0;
                    _referenceFrameManager.AddReferenceFrame(_frameIndexCur);
                    _lastValidFrameIndex = _frameIndexCur;
                    
                    // ✅ 如果成功处理了IDR帧，确保清除参考链断裂标记
                    if (isIdrFrame)
                    {
                        _referenceChainBroken = false;
                    }
                }
                else
                {
                    success = false;
                    _logger?.LogWarning("Video callback did not process frame successfully");
                }

                onFrameReady(composedFrame, recovered, success);
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

