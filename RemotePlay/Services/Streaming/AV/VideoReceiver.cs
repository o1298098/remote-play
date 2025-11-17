using RemotePlay.Services.Streaming.AV.Bitstream;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Utils;
using System;
using System.Collections.Generic;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 视频接收器 - 参考 chiaki-ng 的 ChiakiVideoReceiver
    /// 负责处理视频流，包括 profile 切换、帧索引跟踪、参考帧管理等
    /// </summary>
    public class VideoReceiver
    {
        private readonly ILogger<VideoReceiver>? _logger;
        private readonly FrameProcessor _frameProcessor;
        private readonly ReferenceFrameManager _referenceFrameManager;
        private readonly BitstreamParser _bitstreamParser;

        private VideoProfile[] _profiles = Array.Empty<VideoProfile>();
        private int _profileCur = -1;

        private int _frameIndexCur = -1;
        private int _frameIndexPrev = -1;
        private int _frameIndexPrevComplete = 0;
        private int _framesLost = 0;

        private Action<int, int>? _corruptFrameCallback;

        private readonly object _lock = new();

        public VideoReceiver(ILogger<VideoReceiver>? logger = null)
        {
            _logger = logger;
            _frameProcessor = new FrameProcessor(null); // FrameProcessor2 使用 ILogger<FrameProcessor2>?
            _referenceFrameManager = new ReferenceFrameManager(null); // ReferenceFrameManager 使用 ILogger<ReferenceFrameManager>?
            _bitstreamParser = new BitstreamParser("h264", null); // BitstreamParser 使用 ILogger<BitstreamParser>?
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
        /// 设置视频 profiles（参考 chiaki_video_receiver_stream_info）
        /// </summary>
        public void SetStreamInfo(VideoProfile[] profiles)
        {
            lock (_lock)
            {
                if (_profiles.Length > 0)
                {
                    _logger?.LogError("Video Receiver profiles already set");
                    return;
                }

                _profiles = profiles ?? Array.Empty<VideoProfile>();
                _logger?.LogInformation("Video Profiles: {Count}", _profiles.Length);
                for (int i = 0; i < _profiles.Length; i++)
                {
                    _logger?.LogInformation("  {Index}: {Width}x{Height}", i, _profiles[i].Width, _profiles[i].Height);
                }
            }
        }

        /// <summary>
        /// 处理 AV 包（参考 chiaki_video_receiver_av_packet）
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

                    // 通知 profile 切换（发送新的 header）
                    onFrameReady?.Invoke(newProfile.HeaderWithPadding, false, false);
                }

                // 检测新帧
                if (_frameIndexCur < 0 || (!IsSeq16Older(packet.FrameIndex, _frameIndexCur) && packet.FrameIndex != _frameIndexCur))
                {
                    // 报告上一帧的统计信息
                    // TODO: 如果需要 packet stats

                    // 如果上一帧还没有刷新，先刷新它
                    if (_frameIndexCur >= 0 && _frameIndexPrev != _frameIndexCur)
                    {
                        FlushFrame(onFrameReady);
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
                    }
                }
            }
        }

        /// <summary>
        /// 刷新帧（参考 chiaki_video_receiver_flush_frame）
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

            // 检查 P 帧的参考帧
            BitstreamSlice? slice = null;
            if (frame != null && frameSize > 0)
            {
                BitstreamSlice parsedSlice;
                if (_bitstreamParser.ParseSlice(frame, out parsedSlice))
                {
                    slice = parsedSlice;
                    if (parsedSlice.SliceType == SliceType.P)
                    {
                        int refFrameIndex = _frameIndexCur - (int)parsedSlice.ReferenceFrame - 1;
                        if (parsedSlice.ReferenceFrame != 0xFF && !_referenceFrameManager.HasReferenceFrame(refFrameIndex))
                        {
                            // 尝试查找替代参考帧
                            int alternativeRefFrame = _referenceFrameManager.FindAvailableReferenceFrame(_frameIndexCur, parsedSlice.ReferenceFrame);
                            if (alternativeRefFrame >= 0)
                            {
                                // 尝试修改 bitstream
                                if (_bitstreamParser.SetReferenceFrame(frame, (uint)alternativeRefFrame, out byte[]? modified))
                                {
                                    frame = modified;
                                    recovered = true;
                                    _logger?.LogWarning("Missing reference frame {RefFrame} for decoding frame {Frame} -> changed to {AltRefFrame}",
                                        refFrameIndex, _frameIndexCur, _frameIndexCur - alternativeRefFrame - 1);
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
                    _logger?.LogTrace("Added reference {Type} frame {Frame}", 
                        slice?.SliceType == SliceType.I ? 'I' : 'P', _frameIndexCur);
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

