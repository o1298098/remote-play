using RemotePlay.Models.PlayStation;
using SIPSorcery.Media;
using SIPSorcery.Net;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using RemotePlay.Services;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTC 接收器 - 通过 WebRTC 将 AV 流推送到浏览器
    /// </summary>
    public sealed partial class WebRTCReceiver : IAVReceiver, IDisposable
    {
        private readonly ILogger<WebRTCReceiver> _logger;
        private readonly string _sessionId;
        private readonly LatencyStatisticsService? _latencyStats;
        private RTCPeerConnection? _peerConnection;
        private MediaStreamTrack? _videoTrack;
        private MediaStreamTrack? _audioTrack;
        private bool _disposed;
        private readonly string? _preferredVideoCodec;
        
        // RTP 相关
        private uint _videoSsrc;
        private uint _audioSsrc;
        private ushort _videoSequenceNumber = 0;
        private ushort _audioSequenceNumber = 0;
        
        // ⚠️ 序列号会在 65535 后自动回绕到 0，这是 RTP 协议的正常行为
        // 但需要确保值在 ushort 范围内（0-65535）
        private uint _videoTimestamp = 0;
        private uint _audioTimestamp = 0;
        private readonly DateTime _epochStart = DateTime.UtcNow;
        
        // RTP 会话（通过 MediaStreamTrack 获取）- 未使用，保留以备将来使用
        // private RTPSession? _videoRtpSession;
        // private RTPSession? _audioRtpSession;
        
        // 视频和音频编码器
        private readonly VideoEncoderEndpoint _videoEncoder;
        private readonly AudioEncoderEndpoint _audioEncoder;
        
        // 统计信息
        private int _videoPacketCount;
        private int _audioPacketCount;
        private DateTime _startTime = DateTime.UtcNow;
        
        // 时间戳优化：使用基于实际时间的增量以减少延迟
        private DateTime _lastVideoPacketTime = DateTime.UtcNow;
        private DateTime _lastAudioPacketTime = DateTime.UtcNow;
        
        // 注意：不再需要等待关键帧，WebRTC 会自动处理关键帧检测
        
        // 视频编码格式
        private string _detectedVideoFormat = "h264";
        
        // 音频解码相关（参照 FfmpegMuxReceiver）
        private IOpusDecoder? _opusDecoder;
        private readonly object _opusDecoderLock = new object();
        private int _audioChannels = 2; // 默认 2 声道
        private int _audioFrameSize = 480; // 默认帧大小（10ms @ 48kHz）
        private int _audioSampleRate = 48000;
        private int _sendingAudioChannels = 2; // 实际发送到浏览器的声道数
        private bool _forceStereoDownmix = false;
        private readonly object _opusEncoderLock = new object();
        private OpusEncoder? _stereoOpusEncoder;
        private int _stereoEncoderSampleRate = 48000;
        private byte[] _opusEncodeBuffer = new byte[4096];
        
        // ✅ 音频编解码器选择检测
        private bool _useOpusDirect = true; // 默认尝试直接发送 Opus
        private bool _opusCodecDetected = false; // 是否检测到 Opus 被选中
        
        // RTP 常量
        private const int RTP_MTU = 1200; // RTP MTU（通常比 UDP MTU 小）
        private const uint VIDEO_CLOCK_RATE = 90000; // H.264 视频时钟频率
        private const uint AUDIO_CLOCK_RATE = 48000; // OPUS 音频时钟频率
        private const int VIDEO_FRAME_RATE = 60; // 假设 60fps（用于初始计算）
        private const double VIDEO_TIMESTAMP_INCREMENT = VIDEO_CLOCK_RATE / (double)VIDEO_FRAME_RATE; // 每帧时间戳增量
        
        // ✅ 协商后的动态负载类型（默认 H264=96, HEVC=97，协商成功后将覆盖）
        private int _negotiatedPtH264 = 96;
        private int _negotiatedPtHevc = 97;
        
        public event EventHandler? OnDisconnected;
        
        // ✅ 关键帧请求事件：当收到来自浏览器的 RTCP PLI/FIR 反馈时触发
        public event EventHandler? OnKeyframeRequested;
        
        // 帧索引跟踪（用于延时统计）
        private long _currentVideoFrameIndex = 0;
        private long _currentAudioFrameIndex = 0;
        
        // ✅ 性能优化：缓存反射方法，避免每次发送时查找
        private System.Reflection.MethodInfo? _cachedSendVideoMethod;
        private System.Reflection.MethodInfo? _cachedSendRtpRawMethod;
        private System.Reflection.MethodInfo? _cachedSendRtpRawVideoMethod;
        private System.Reflection.MethodInfo? _cachedSendRtpRawAudioMethod;
        private bool _methodsInitialized = false;
        private readonly object _methodsLock = new object();
        
        // ✅ 性能优化：缓存连接状态，减少属性访问开销
        private RTCPeerConnectionState? _cachedConnectionState;
        private RTCIceConnectionState? _cachedIceState;
        private RTCSignalingState? _cachedSignalingState;
        private DateTime _lastStateCheckTime = DateTime.MinValue;
        private const int STATE_CACHE_MS = 50; // 状态缓存50ms（视频60fps时每帧16ms）
        private readonly List<(object target, EventInfo @event, Delegate handler)> _rtcpFeedbackSubscriptions = new();
        private readonly HashSet<string> _rtcpSubscribedEventKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _rtcpFeedbackLock = new();
        private bool _rtcpFeedbackSubscribed;
        private DateTime _lastKeyframeRequestTime = DateTime.MinValue;
        private static readonly TimeSpan KEYFRAME_REQUEST_COOLDOWN = TimeSpan.FromMilliseconds(500);
        
        public WebRTCReceiver(
            string sessionId,
            RTCPeerConnection peerConnection,
            ILogger<WebRTCReceiver> logger,
            LatencyStatisticsService? latencyStats = null,
            string? preferredVideoCodec = null)
        {
            _sessionId = sessionId;
            _peerConnection = peerConnection;
            _logger = logger;
            _latencyStats = latencyStats;
            _preferredVideoCodec = NormalizePreferredVideoCodec(preferredVideoCodec);
            
            _videoEncoder = new VideoEncoderEndpoint();
            _audioEncoder = new AudioEncoderEndpoint();
            
            _logger.LogInformation("🎬 WebRTCReceiver 初始化 - SessionId: {SessionId}", _sessionId);
            
            // 生成随机 SSRC
            var random = new Random();
            _videoSsrc = (uint)random.Next(1, int.MaxValue);
            _audioSsrc = (uint)random.Next(1, int.MaxValue);
            
            // ✅ 初始化时缓存反射方法（避免每次发送时查找）
            InitializeReflectionMethods();
            
            // 监听连接状态变化（同时更新缓存）
            _peerConnection.onconnectionstatechange += (state) =>
            {
                _cachedConnectionState = state;
                _lastStateCheckTime = DateTime.UtcNow;
                _logger.LogInformation("📡 WebRTC 连接状态变化: {State} (当前视频包数: {Count}, ICE状态: {IceState})", 
                    state, _videoPacketCount, _peerConnection.iceConnectionState);
                if (state == RTCPeerConnectionState.connected)
                {
                    // 连接建立后，获取 RTP 通道
                    InitializeRtpChannels();
                    
                    // ✅ 解析 SDP，获取浏览器协商的 H264/HEVC Payload Type
                    TryDetectNegotiatedVideoPayloadTypes();
                    
                    // ✅ 检测浏览器实际选择的音频编解码器
                    DetectSelectedAudioCodec();
                }
                else if (state == RTCPeerConnectionState.failed || 
                    state == RTCPeerConnectionState.disconnected ||
                    state == RTCPeerConnectionState.closed)
                {
                    _logger.LogWarning("⚠️ WebRTC 连接断开: {State}", state);
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    // ✅ 连接恢复时，如果之前是断开状态，可能需要请求关键帧
                    // 这可以处理网络中断后的恢复场景
                    if (_cachedConnectionState == RTCPeerConnectionState.disconnected ||
                        _cachedConnectionState == RTCPeerConnectionState.failed)
                    {
                        _logger.LogInformation("🔄 连接已恢复，请求关键帧以同步视频流");
                        OnKeyframeRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            };
            
            // 监听 ICE 连接状态变化
            _peerConnection.oniceconnectionstatechange += (state) =>
            {
                _cachedIceState = state;
                _lastStateCheckTime = DateTime.UtcNow;
                
                // 如果 ICE 已连接，但 connectionState 还是 new，记录警告
                if (state == RTCIceConnectionState.connected &&
                    _peerConnection.connectionState == RTCPeerConnectionState.@new)
                {
                    _logger.LogWarning("⚠️ ICE 已连接但 connectionState 仍是 new，这可能影响视频发送");
                }
            };
            
            // 监听 ICE gathering 状态变化
            _peerConnection.onicegatheringstatechange += (state) =>
            {
                // ICE gathering 状态变化日志已移除
            };
            
            // 监听 ICE candidates
            _peerConnection.onicecandidate += (candidate) =>
            {
                // ICE candidate 日志已移除
            };
            
            // ✅ 监听 RTCP 反馈（PLI/FIR 关键帧请求）
            InitializeRTCPFeedback();
            
            // 创建视频和音频轨道
            InitializeTracks();
        }
        
        /// <summary>
        /// 从 SDP 中解析 H264/H265 的动态负载类型（payload type）
        /// </summary>
        private void TryDetectNegotiatedVideoPayloadTypes()
        {
            try
            {
                string sdp = "";
                if (_peerConnection?.localDescription?.sdp != null)
                {
                    sdp = _peerConnection.localDescription.sdp.ToString() ?? "";
                }
                // 若本地为空，尝试远端
                if (string.IsNullOrWhiteSpace(sdp) && _peerConnection?.remoteDescription?.sdp != null)
                {
                    sdp = _peerConnection.remoteDescription.sdp.ToString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(sdp))
                {
                    return;
                }
                
                // 解析 a=rtpmap:<pt> H264/90000 或 H265/90000
                var lines = sdp.Split('\n');
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // a=rtpmap:96 H264/90000
                    var parts = line.Substring("a=rtpmap:".Length).Split(' ');
                    if (parts.Length < 2) continue;
                    if (!int.TryParse(parts[0], out var pt)) continue;
                    
                    var codecPart = parts[1].ToLowerInvariant();
                    if (codecPart.StartsWith("h264/"))
                    {
                        _negotiatedPtH264 = pt;
                        _logger.LogInformation("✅ 协商的 H264 PayloadType: {Pt}", pt);
                    }
                    else if (codecPart.StartsWith("h265/") || codecPart.StartsWith("hevc/"))
                    {
                        _negotiatedPtHevc = pt;
                        _logger.LogInformation("✅ 协商的 HEVC PayloadType: {Pt}", pt);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "⚠️ 解析视频 PayloadType 失败，继续使用默认值 (H264=96, HEVC=97)");
            }
        }
        
        /// <summary>
        /// 检测浏览器实际选择的音频编解码器
        /// </summary>
        private void DetectSelectedAudioCodec()
        {
            try
            {
                if (_peerConnection == null) return;
                
                // 获取 remote description (Answer SDP)
                var remoteDescription = _peerConnection.remoteDescription;
                if (remoteDescription == null || remoteDescription.sdp == null)
                {
                    _logger.LogWarning("⚠️ 无法检测音频编解码器：remote description 为空");
                    _useOpusDirect = false; // 回退到转码
                    return;
                }
                
                // ✅ SDP 对象需要转换为字符串
                string sdp = remoteDescription.sdp.ToString() ?? "";
                if (string.IsNullOrEmpty(sdp))
                {
                    _logger.LogWarning("⚠️ 无法检测音频编解码器：SDP 字符串为空");
                    _useOpusDirect = false; // 回退到转码
                    return;
                }
                
                // 检查是否包含 Opus
                bool hasOpus = sdp.Contains("opus") || sdp.Contains("111");
                bool hasPCMU = sdp.Contains("PCMU") || sdp.Contains("a=rtpmap:0");
                
                _logger.LogInformation("🔊 检测到的音频编解码器: Opus={Opus}, PCMU={PCMU}", hasOpus, hasPCMU);
                
                // 查找 m=audio 行，检查浏览器选择的编解码器
                var lines = sdp.Split('\n');
                bool inAudioSection = false;
                string? selectedPayloadType = null;
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("m=audio"))
                    {
                        inAudioSection = true;
                        // m=audio 格式: m=audio <port> <proto> <fmt> <fmt> ...
                        // 第一个 fmt 是浏览器选择的编解码器
                        var parts = trimmed.Split(' ');
                        if (parts.Length > 3)
                        {
                            selectedPayloadType = parts[3]; // 第一个格式（浏览器选择的）
                            _logger.LogInformation("🔊 m=audio 行第一个格式（浏览器选择）: {Format}", selectedPayloadType);
                        }
                    }
                    else if (trimmed.StartsWith("m=") && !trimmed.StartsWith("m=audio"))
                    {
                        inAudioSection = false;
                    }
                    else if (inAudioSection && trimmed.StartsWith("a=rtpmap:"))
                    {
                        // a=rtpmap:111 opus/48000/2 或 a=rtpmap:0 PCMU/8000/1
                        var parts = trimmed.Split(' ');
                        if (parts.Length > 1)
                        {
                            var payloadTypeStr = parts[0].Substring("a=rtpmap:".Length);
                            if (payloadTypeStr == selectedPayloadType)
                            {
                                // 这是浏览器选择的编解码器
                                if (trimmed.Contains("opus"))
                                {
                                    _opusCodecDetected = true;
                                    _logger.LogInformation("✅ 浏览器选择了 Opus（高质量）: {Line}", trimmed);
                                }
                                else if (trimmed.Contains("PCMU") || payloadTypeStr == "0")
                                {
                                    _opusCodecDetected = false;
                                    _logger.LogWarning("⚠️ 浏览器选择了 PCMU: {Line}", trimmed);
                                }
                                else
                                {
                                    _opusCodecDetected = false;
                                    _logger.LogWarning("⚠️ 浏览器选择了其他编解码器: {Line}", trimmed);
                                }
                            }
                        }
                    }
                }
                
                // ✅ 优化策略：即使浏览器选择了 PCMU，也优先尝试发送 Opus
                // 现代浏览器通常能处理 Opus，即使 SDP 中也选择了 PCMU
                _useOpusDirect = _opusCodecDetected;
                
                // ✅ 如果浏览器选择了 PCMU，标记为需要尝试 Opus（高质量）
                if (hasPCMU && selectedPayloadType == "0")
                {
                    _opusCodecDetected = false;
                    // 不强制使用转码，而是尝试发送 Opus（高质量）
                    _useOpusDirect = false; // 标记为 false，但会在发送时尝试 Opus
                    _logger.LogInformation("🔄 浏览器选择了 PCMU，但将尝试发送 Opus 以获得高质量音质");
                }
                
                // ✅ 如果检测到 Opus，直接使用；否则标记为需要尝试 Opus
                if (_opusCodecDetected)
                {
                    _useOpusDirect = true;
                }
                else
                {
                    // 未检测到 Opus，但会尝试发送 Opus（通过 TrySendOpusReencoded）
                    _useOpusDirect = false;
                }
                
                if (!_useOpusDirect)
                {
                    _logger.LogWarning("⚠️ 浏览器选择了 PCMU（8kHz），将尝试发送 Opus 以获得高质量音质，如果失败则使用转码方案");
                }
                else
                {
                    _logger.LogInformation("✅ 浏览器选择了 Opus（48kHz，高质量编码），将直接发送 Opus 数据，无需转码");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 检测音频编解码器失败，默认使用转码方案");
                _useOpusDirect = false; // 出错时回退到转码
            }
        }
        
        private void InitializeTracks()
        {
            // ✅ 更新：Chrome 136+ 默认支持 WebRTC H265/HEVC 编码
            // 因此同时提供 H.264 和 HEVC 支持，让浏览器选择它支持的格式
            // Chrome 136+ 会选择 HEVC，旧版本浏览器会选择 H.264
            
            var h264Format = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                96,
                "H264",
                90000
            );
            
            // ✅ 添加 HEVC 支持（Chrome 136+ 支持）
            var hevcFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                97,
                "H265",
                90000
            );
            
            // 同时提供 H.264 和 HEVC，让浏览器选择
            // Chrome 136+ 会选择 HEVC，旧版本浏览器会选择 H.264
            var videoFormats = BuildVideoFormats(h264Format, hevcFormat);
            
            
            _videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                false,
                videoFormats,
                MediaStreamStatusEnum.SendOnly
            );
            
            // ✅ 优化：优先使用 Opus，获得最高音质（48kHz，高质量编码）
            // Opus 是 WebRTC 标准编解码器，所有现代浏览器都支持
            // 提供 PCMU 作为备用以确保兼容性，但优先使用 Opus
            var initialAudioChannels = Math.Max(1, _sendingAudioChannels);
            var opusFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.audio,
                111,
                "opus",
                48000,
                initialAudioChannels
            );
            
            // 提供 PCMU 作为备用（兼容性，但会降低音质）
            var pcmuFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.audio,
                0,
                "PCMU",
                8000
            );
            
            // ✅ 优先提供 Opus，确保浏览器优先选择高质量编码
            // 如果浏览器不支持 Opus，会回退到 PCMU（然后使用转码）
            _audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio,
                false,
                new List<SDPAudioVideoMediaFormat> { 
                    opusFormat,
                    //pcmuFormat 
                 },
                MediaStreamStatusEnum.SendRecv
            );
            
            
            // ✅ 添加视频和音频轨道
            _peerConnection?.addTrack(_videoTrack);
            _peerConnection?.addTrack(_audioTrack);
            
        }

        private static string? NormalizePreferredVideoCodec(string? codec)
        {
            if (string.IsNullOrWhiteSpace(codec))
            {
                return null;
            }

            var normalized = codec.Trim().ToLowerInvariant();
            return normalized switch
            {
                "h264" => "h264",
                "avc" => "h264",
                "h265" => "h265",
                "hevc" => "h265",
                _ => null
            };
        }

        private List<SDPAudioVideoMediaFormat> BuildVideoFormats(
            SDPAudioVideoMediaFormat h264Format,
            SDPAudioVideoMediaFormat hevcFormat)
        {
            if (_preferredVideoCodec.ToLower() == "h264" || _preferredVideoCodec.ToLower() == "avc")
            {
                _logger.LogInformation("🎯 WebRTC 视频轨道使用首选编码：H.264");
                return new List<SDPAudioVideoMediaFormat> { h264Format };
            }

            if (_preferredVideoCodec.ToLower() == "h265"|| _preferredVideoCodec.ToLower() == "hevc")
            {
                _logger.LogInformation("🎯 WebRTC 视频轨道使用首选编码：H.265/HEVC");
                return new List<SDPAudioVideoMediaFormat> { hevcFormat,h264Format };
            }

            // 未指定时，保持默认顺序：HEVC 优先，H.264 备用
            return new List<SDPAudioVideoMediaFormat> { hevcFormat, h264Format };
        }
        
        public void SetVideoCodec(string codec)
        {
            _detectedVideoFormat = codec.ToLower();
            _logger.LogInformation("📹 视频编码格式: {Codec}", codec);
        }
        
        public void SetAudioCodec(string codec)
        {
            // 音频编码格式已设置
        }
        
        public void EnterWaitForIdr()
        {
            // ✅ 当需要等待关键帧时，触发关键帧请求事件
            // 这通常发生在切换接收器或重新连接时
            _logger.LogInformation("🎬 进入等待 IDR 模式，请求关键帧");
            OnKeyframeRequested?.Invoke(this, EventArgs.Empty);
        }
        
        public void OnStreamInfo(byte[] videoHeader, byte[] audioHeader)
        {
            try
            {
                // 处理视频 header（检测编码格式）
                if (videoHeader != null && videoHeader.Length > 0)
                {
                    string? detectedCodec = DetectCodecFromVideoHeader(videoHeader);
                    if (detectedCodec != null && detectedCodec != _detectedVideoFormat)
                    {
                        _detectedVideoFormat = detectedCodec;
                    }
                }
                
                // ⚠️ 参照 FfmpegMuxReceiver：从 audioHeader 读取音频参数
                if (audioHeader != null && audioHeader.Length >= 10)
                {
                    int channels = ParseAudioChannels(audioHeader);
                    int bitsPerSample = ParseBitsPerSample(audioHeader);
                    int rate = ParseSampleRate(audioHeader);
                    int frameSize = ParseFrameSize(audioHeader);
                    
                    // 保存帧大小（用于 PCM 缓冲区大小计算）
                    if (frameSize > 0)
                    {
                        _audioFrameSize = frameSize;
                    }
                    int previousSourceChannels = _audioChannels;

                    if (channels > 0)
                    {
                        if (_audioPacketCount < 5 || previousSourceChannels != channels)
                        {
                            _logger.LogInformation("🔊 音频参数：channels={Channels}, bits={Bits}, rate={Rate}Hz, frameSize={FrameSize}", channels, bitsPerSample, rate, frameSize);
                        }

                        if (channels != 2 && (_audioPacketCount < 5 || previousSourceChannels != channels))
                        {
                            _logger.LogWarning("⚠️ 主机报告音频声道数为 {Channels}，建议在主机端开启立体声下混或设置为 2 声道输出", channels);
                        }

                        _audioChannels = Math.Clamp(channels, 1, 2);
                        _forceStereoDownmix = false;
                        _useOpusDirect = true;
                        _sendingAudioChannels = 2;
                    }

                    // 初始化 Opus 解码器（参照 FfmpegMuxReceiver）
                    if (rate > 0 && channels > 0)
                    {
                        lock (_opusDecoderLock)
                        {
                            // 如果参数改变，重新初始化解码器
                            bool needReinit = false;
                            if (rate != _audioSampleRate)
                            {
                                _audioSampleRate = rate;
                                needReinit = true;
                            }
                            if (rate != _audioSampleRate)
                            {
                                _audioSampleRate = rate;
                                needReinit = true;
                            }
                            int targetChannels = Math.Clamp(channels, 1, 2);
                            if (targetChannels != _audioChannels)
                            {
                                _audioChannels = targetChannels;
                                needReinit = true;
                            }

                            if (needReinit || _opusDecoder == null)
                            {
                                _opusDecoder?.Dispose();
                                try
                                {
                                    _opusDecoder = OpusCodecFactory.CreateDecoder(_audioSampleRate, _audioChannels);
            }
            catch (Exception ex)
            {
                                    _logger.LogError(ex, "❌ 初始化 Opus 解码器失败");
                                    _opusDecoder = null;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理 StreamInfo 失败");
            }
        }
        
        public void OnAudioPacket(byte[] packet)
        {
            // ✅ 优化：直接发送 Opus RTP 包，不转码，保持原始音质
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    return;
                }
                
                // 记录数据包到达时间（用于延时统计）
                var arrivalTime = DateTime.UtcNow;
                _currentAudioFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "audio", _currentAudioFrameIndex);
                
                // 发送音频包到 WebRTC
                SendAudioPacketInternal(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频包失败");
            }
        }
        
        /// <summary>
        /// 转码并发送音频：Opus -> PCM -> PCMU (G.711 μ-law)
        /// </summary>
        private void SendAudioPacketInternal(byte[] packet)
        {
            try
            {
                if (_peerConnection == null || packet == null || packet.Length <= 1)
                {
                    return;
                }
                
                // packet 格式：[HeaderType.AUDIO (1 byte)] + [编码后音频帧]
                var payloadType = (HeaderType)packet[0];
                if (payloadType != HeaderType.AUDIO)
                {
                    _logger.LogWarning("⚠️ 非音频包传入 OnAudioPacket，已忽略");
                            return;
                        }

                var opusFrame = packet.AsSpan(1).ToArray();

                if (_forceStereoDownmix)
                {
                    if (TrySendOpusDownmixedToStereo(opusFrame, out var downmixedFrame))
                    {
                        SendAudioOpusDirect(downmixedFrame.FrameData, downmixedFrame.SamplesPerFrame);
                    }
                    else
                    {
                        SendAudioOpusDirect(opusFrame);
                    }
                }
                else
                {
                    SendAudioOpusDirect(opusFrame);
                }

                _latencyStats?.RecordPacketSent(_sessionId, "audio", _currentAudioFrameIndex);
                _audioPacketCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理音频包失败");
            }
        }
        
        /// <summary>
        /// 高质量降采样 PCM 音频（使用双级降采样 + 多级抗混叠滤波，最大程度减少噪音）
        /// ✅ 使用双级降采样（48kHz -> 16kHz -> 8kHz）和多级滤波，显著减少混叠和噪音
        /// </summary>
        private byte[] DownsamplePCM(byte[] pcmData, int sourceRate, int targetRate, int channels)
        {
            if (pcmData == null || pcmData.Length == 0 || sourceRate <= 0 || targetRate <= 0 || channels <= 0)
            {
                return Array.Empty<byte>();
            }
            
            // 计算降采样比例
            int ratio = sourceRate / targetRate; // 48000 / 8000 = 6
            if (ratio <= 1)
            {
                return pcmData; // 不需要降采样
            }
            
            // ✅ 优化：使用双级降采样（48kHz -> 16kHz -> 8kHz），减少混叠
            // 第一步：48kHz -> 16kHz (ratio = 3)
            byte[]? intermediatePcm = null;
            if (sourceRate == 48000 && targetRate == 8000)
            {
                // 先降到 16kHz
                intermediatePcm = DownsamplePCMSingleStage(pcmData, sourceRate, 16000, channels);
                if (intermediatePcm == null || intermediatePcm.Length == 0)
                {
                    return Array.Empty<byte>();
                }
                // 再从 16kHz 降到 8kHz
                return DownsamplePCMSingleStage(intermediatePcm, 16000, targetRate, channels);
            }
            else
            {
                // 单级降采样
                return DownsamplePCMSingleStage(pcmData, sourceRate, targetRate, channels);
            }
        }
        
        /// <summary>
        /// 单级降采样（使用高质量 FIR 滤波）
        /// </summary>
        private byte[] DownsamplePCMSingleStage(byte[] pcmData, int sourceRate, int targetRate, int channels)
        {
            if (pcmData == null || pcmData.Length == 0 || sourceRate <= 0 || targetRate <= 0 || channels <= 0)
            {
                return Array.Empty<byte>();
            }
            
            int ratio = sourceRate / targetRate;
            if (ratio <= 1)
            {
                return pcmData;
            }
            
            int sourceSamples = pcmData.Length / (2 * channels);
            int targetSamples = sourceSamples / ratio;
            
            if (targetSamples == 0)
            {
                return Array.Empty<byte>();
            }
            
            // ✅ 使用更高阶的 11 点 FIR 低通滤波器（权重 [1, 2, 3, 4, 5, 6, 5, 4, 3, 2, 1]）
            // 这能提供更陡峭的频响特性，更有效地去除高频噪音和混叠，同时保持更好的音质
            byte[] filteredPcm = new byte[pcmData.Length];
            Buffer.BlockCopy(pcmData, 0, filteredPcm, 0, pcmData.Length);
            
            // ✅ 安全代码：使用 Span<T> 和 MemoryMarshal 进行高效处理
            var sourceShortSpan = MemoryMarshal.Cast<byte, short>(pcmData.AsSpan());
            var filteredShortSpan = MemoryMarshal.Cast<byte, short>(filteredPcm.AsSpan());
            
            // ✅ 对每个声道应用 11 点 FIR 低通滤波（高质量）
            for (int ch = 0; ch < channels; ch++)
            {
                // 11 点加权平均：[1, 2, 3, 4, 5, 6, 5, 4, 3, 2, 1] / 36
                // 这种滤波器能提供更陡峭的截止频率，更好地保留音频细节
                for (int i = 5; i < sourceSamples - 5; i++)
                {
                    int offset = i * channels + ch;
                    long sum = (long)sourceShortSpan[(i - 5) * channels + ch] +
                               (long)sourceShortSpan[(i - 4) * channels + ch] * 2 +
                               (long)sourceShortSpan[(i - 3) * channels + ch] * 3 +
                               (long)sourceShortSpan[(i - 2) * channels + ch] * 4 +
                               (long)sourceShortSpan[(i - 1) * channels + ch] * 5 +
                               (long)sourceShortSpan[offset] * 6 +
                               (long)sourceShortSpan[(i + 1) * channels + ch] * 5 +
                               (long)sourceShortSpan[(i + 2) * channels + ch] * 4 +
                               (long)sourceShortSpan[(i + 3) * channels + ch] * 3 +
                               (long)sourceShortSpan[(i + 4) * channels + ch] * 2 +
                               (long)sourceShortSpan[(i + 5) * channels + ch];
                    filteredShortSpan[offset] = (short)(sum / 36);
                }
            }
            
            // ✅ 使用线性插值重采样（而不是简单平均），提供更高质量的重采样
            byte[] downsampled = new byte[targetSamples * 2 * channels];
            
            // ✅ 安全代码：使用 Span<T> 和 MemoryMarshal 进行高效处理
           
            var targetShortSpan = MemoryMarshal.Cast<byte, short>(downsampled.AsSpan());
            
            // 使用线性插值进行重采样，提供更平滑的过渡
            double step = (double)sourceSamples / targetSamples;
            
            for (int i = 0; i < targetSamples; i++)
            {
                double sourcePos = i * step;
                int sourceIndex = (int)sourcePos;
                double fraction = sourcePos - sourceIndex;
                
                for (int ch = 0; ch < channels; ch++)
                {
                    int targetOffset = i * channels + ch;
                    
                    if (sourceIndex + 1 < sourceSamples)
                    {
                        // 线性插值：在两个样本之间进行插值
                        int offset1 = sourceIndex * channels + ch;
                        int offset2 = (sourceIndex + 1) * channels + ch;
                        
                        double sample1 = filteredShortSpan[offset1];
                        double sample2 = filteredShortSpan[offset2];
                        
                        // 线性插值公式：result = sample1 + (sample2 - sample1) * fraction
                        double interpolated = sample1 + (sample2 - sample1) * fraction;
                        targetShortSpan[targetOffset] = (short)Math.Round(interpolated);
                    }
                    else if (sourceIndex < sourceSamples)
                    {
                        // 边界情况：使用最后一个样本
                        int offset = sourceIndex * channels + ch;
                        targetShortSpan[targetOffset] = filteredShortSpan[offset];
                    }
                }
            }
            
            return downsampled;
        }
        
        /// <summary>
        /// 将 PCM (16-bit signed) 编码为 PCMA (G.711 A-law)
        /// A-law 在低音量时音质比 μ-law 更好
        /// 注意：PCMA 通常使用单声道，如果是立体声，需要先转换为单声道
        /// </summary>
        private byte[] EncodePCMToPCMA(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length == 0 || pcmData.Length % 2 != 0)
            {
                return Array.Empty<byte>();
            }
            
            // ⚠️ PCMA 通常使用单声道，如果是立体声，需要先混合为单声道
            byte[] monoPcm = pcmData;
            int channels = _audioChannels;
            
            if (channels > 1)
            {
                // 将立体声混合为单声道（简单平均）
                int sampleCount = pcmData.Length / (2 * channels);
                monoPcm = new byte[sampleCount * 2];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    long sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = (i * channels + ch) * 2;
                        if (offset + 1 < pcmData.Length)
                        {
                            short sample = (short)(pcmData[offset] | (pcmData[offset + 1] << 8));
                            sum += sample;
                        }
                    }
                    
                    short monoSample = (short)(sum / channels);
                    monoPcm[i * 2] = (byte)(monoSample & 0xFF);
                    monoPcm[i * 2 + 1] = (byte)((monoSample >> 8) & 0xFF);
                }
            }
            
            int sampleCountFinal = monoPcm.Length / 2;
            byte[] pcmaData = new byte[sampleCountFinal];
            
            for (int i = 0; i < sampleCountFinal; i++)
            {
                // 读取 16-bit signed PCM 样本（little-endian）
                short pcmSample = (short)(monoPcm[i * 2] | (monoPcm[i * 2 + 1] << 8));
                
                // 编码为 A-law
                pcmaData[i] = EncodeALaw(pcmSample);
            }
            
            return pcmaData;
        }
        
        /// <summary>
        /// G.711 A-law 编码（将 16-bit signed PCM 样本编码为 8-bit A-law）
        /// A-law 在低音量时音质比 μ-law 更好
        /// 使用标准 ITU-T G.711 算法
        /// </summary>
        private byte EncodeALaw(short pcmSample)
        {
            // A-law 编码算法（标准 G.711）
            // 获取符号位
            int sign = (pcmSample & 0x8000) != 0 ? 0x80 : 0x00;
            
            // 如果是负数，取绝对值
            int magnitude = pcmSample;
            if (magnitude < 0)
            {
                magnitude = -magnitude;
            }
            
            // ⚠️ 修复：A-law 使用 13 位范围（0-8191），但实际编码时使用不同的分段
            // 限制范围到 8191（13 位）
            if (magnitude > 8191)
            {
                magnitude = 8191;
            }
            
            // A-law 编码：使用分段线性量化
            // 标准 G.711 A-law 算法（不需要添加偏置，与 μ-law 不同）
            int exponent = 0;
            int mantissa = 0;
            
            // ⚠️ A-law 不使用偏置，直接处理 magnitude
            
            // 查找指数（exponent）- 标准 A-law 算法
            // A-law 使用 13 位分段，每段 16 个量化级别
            if (magnitude >= 256)
            {
                // 高段：256-8191
                if (magnitude >= 4096)
                {
                    exponent = 7;
                    mantissa = (magnitude >> 7) & 0x0F;
                }
                else if (magnitude >= 2048)
                {
                    exponent = 6;
                    mantissa = (magnitude >> 6) & 0x0F;
                }
                else if (magnitude >= 1024)
                {
                    exponent = 5;
                    mantissa = (magnitude >> 5) & 0x0F;
                }
                else if (magnitude >= 512)
                {
                    exponent = 4;
                    mantissa = (magnitude >> 4) & 0x0F;
                }
                else
                {
                    exponent = 3;
                    mantissa = (magnitude >> 3) & 0x0F;
                }
            }
            else
            {
                // 低段：0-255
                if (magnitude >= 128)
                {
                    exponent = 2;
                    mantissa = (magnitude >> 2) & 0x0F;
                }
                else if (magnitude >= 64)
                {
                    exponent = 1;
                    mantissa = (magnitude >> 1) & 0x0F;
                }
                else
                {
                    exponent = 0;
                    mantissa = magnitude & 0x0F;
                }
            }
            
            // 组合为 A-law 字节：符号位(1) + 指数(3) + 尾数(4)
            // 格式：S EEE MMMM
            byte alaw = (byte)(sign | (exponent << 4) | mantissa);
            
            // A-law 特性：偶数位取反（与 μ-law 不同，μ-law 是所有位取反）
            return (byte)(alaw ^ 0x55);
        }
        
        /// <summary>
        /// 快速将 PCM (16-bit signed) 编码为 PCMU (G.711 μ-law)
        /// ✅ 优化：使用 unsafe 代码和合并声道转换以提升速度
        /// </summary>
        private byte[] EncodePCMToPCMU(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length == 0 || pcmData.Length % 2 != 0)
            {
                return Array.Empty<byte>();
            }
            
            int channels = _audioChannels;
            int sampleCount = pcmData.Length / (2 * channels);
            byte[] pcmuData = new byte[sampleCount];
            
            // ✅ 优化：在编码过程中同时处理单声道转换，减少遍历次数
            // ✅ 安全代码：使用 Span<T> 和 MemoryMarshal 进行高效处理
            var pcmShortSpan = MemoryMarshal.Cast<byte, short>(pcmData.AsSpan());
            
            if (channels > 1)
            {
                // ✅ 优化音质：立体声混合为单声道时使用更精确的算法
                // 避免简单的平均造成的精度损失
                for (int i = 0; i < sampleCount; i++)
                {
                    // 使用双精度累加，避免精度损失
                    double sum = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += pcmShortSpan[i * channels + ch];
                    }
                    // 四舍五入而不是截断，提升精度
                    short monoSample = (short)Math.Round(sum / channels);
                    pcmuData[i] = EncodeMuLaw(monoSample);
                }
            }
            else
            {
                // 单声道：直接编码
                for (int i = 0; i < sampleCount; i++)
                {
                    pcmuData[i] = EncodeMuLaw(pcmShortSpan[i]);
                }
            }
            
            return pcmuData;
        }
        
        /// <summary>
        /// 快速 G.711 μ-law 编码（优化版本）
        /// ✅ 使用查找表和位操作优化，提升编码速度
        /// </summary>
        private byte EncodeMuLaw(short pcmSample)
        {
            // ✅ 优化：使用更高效的位操作和查找表
            int sign = (pcmSample & 0x8000) >> 8; // 符号位移到位置 7
            
            // 取绝对值并限制范围
            int magnitude = pcmSample < 0 ? -pcmSample : pcmSample;
            if (magnitude > 32635) magnitude = 32635;
            
            // 添加偏置并查找最高位（使用位操作）
            magnitude += 33;
            
            // ✅ 优化：使用位操作查找最高位，比循环更快
            int exponent = 7;
            if ((magnitude & 0x7F00) != 0) exponent = 7;
            else if ((magnitude & 0x0780) != 0) exponent = 6;
            else if ((magnitude & 0x03C0) != 0) exponent = 5;
            else if ((magnitude & 0x01E0) != 0) exponent = 4;
            else if ((magnitude & 0x00F0) != 0) exponent = 3;
            else if ((magnitude & 0x0078) != 0) exponent = 2;
            else if ((magnitude & 0x003C) != 0) exponent = 1;
            else exponent = 0;
            
            // 计算尾数
            int mantissa = (magnitude >> (exponent + 3)) & 0x0F;
            
            // 组合并取反
            return (byte)(~(sign | (exponent << 4) | mantissa));
        }
        
        /// <summary>
        /// 将 PCMA 数据打包为 RTP 并发送（payload type = 8）
        /// </summary>
        private void SendAudioPCMAAsRTP(byte[] pcmaData, int samplesDecoded)
        {
            try
            {
                if (_peerConnection == null || pcmaData == null || pcmaData.Length == 0)
                {
                    return;
                }
                
                // ⚠️ 放宽发送条件：即使信令状态是 have_local_offer，也尝试发送
                var connectionState = _peerConnection.connectionState;
                var iceState = _peerConnection.iceConnectionState;
                var signalingState = _peerConnection.signalingState;
                
                bool canSend = signalingState == RTCSignalingState.stable ||
                               (signalingState == RTCSignalingState.have_local_offer && 
                                (iceState == RTCIceConnectionState.connected || 
                                 iceState == RTCIceConnectionState.checking ||
                                 connectionState == RTCPeerConnectionState.connected ||
                                 connectionState == RTCPeerConnectionState.connecting));
                
                if (iceState == RTCIceConnectionState.@new && signalingState == RTCSignalingState.have_local_offer)
                {
                    canSend = true; // 即使 ICE 是 new，也尝试发送
                }
                
                if (!canSend)
                {
                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                    {
                        _logger.LogDebug("⏳ 等待音频通道就绪: signaling={Signaling}, connection={Connection}, ICE={Ice}", 
                            signalingState, connectionState, iceState);
                    }
                    return;
                }
                
                // 更新时间戳（PCMA 是 8000Hz）
                // samplesDecoded 已经是降采样后的样本数（8000Hz），直接使用
                if (samplesDecoded == 0)
                {
                    samplesDecoded = 160; // 默认 20ms @ 8000Hz = 160 样本
                }
                _audioTimestamp += (uint)samplesDecoded;
                
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + pcmaData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 8; // PCMA (G.711 A-law) payload type
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                // 复制 PCMA 数据到 payload
                Buffer.BlockCopy(pcmaData, 0, rtpPacket.Payload, 0, pcmaData.Length);
                
                // 尝试发送 RTP 包
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmaData, 8); // payload type = 8 (PCMA)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCMA RTP 包失败");
            }
        }
        
        /// <summary>
        /// 将 PCMU 数据打包为 RTP 并发送（payload type = 0）
        /// </summary>
        private void SendAudioPCMUAsRTP(byte[] pcmuData, int samplesDecoded)
        {
            try
            {
                if (_peerConnection == null || pcmuData == null || pcmuData.Length == 0)
                {
                    return;
                }
                
                // ⚠️ 放宽发送条件：即使信令状态是 have_local_offer，也尝试发送
                var connectionState = _peerConnection.connectionState;
                var iceState = _peerConnection.iceConnectionState;
                var signalingState = _peerConnection.signalingState;
                
                bool canSend = signalingState == RTCSignalingState.stable ||
                               (signalingState == RTCSignalingState.have_local_offer && 
                                (iceState == RTCIceConnectionState.connected || 
                                 iceState == RTCIceConnectionState.checking ||
                                 connectionState == RTCPeerConnectionState.connected ||
                                 connectionState == RTCPeerConnectionState.connecting));
                
                if (iceState == RTCIceConnectionState.@new && signalingState == RTCSignalingState.have_local_offer)
                {
                    canSend = true; // 即使 ICE 是 new，也尝试发送
                }
                
                if (!canSend)
                {
                    return;
                }
                
                // 更新时间戳（PCMU 是 8000Hz）
                // samplesDecoded 已经是降采样后的样本数（8000Hz），直接使用
                if (samplesDecoded == 0)
                {
                    samplesDecoded = 160; // 默认 20ms @ 8000Hz = 160 样本
                }
                _audioTimestamp += (uint)samplesDecoded;
                
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + pcmuData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 0; // PCMU (G.711 μ-law) payload type
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                // 复制 PCMU 数据到 payload
                Buffer.BlockCopy(pcmuData, 0, rtpPacket.Payload, 0, pcmuData.Length);
                
                // 尝试发送 RTP 包
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmuData, 0); // payload type = 0 (PCMU)
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCMU RTP 包失败");
            }
        }
        
        /// <summary>
        /// 发送音频 PCM 数据（参照 FfmpegMuxReceiver：将 Opus 解码为 PCM 后发送）
        /// </summary>
        private void SendAudioPCM(byte[] opusFrame)
        {
            try
            {
                if (_peerConnection == null || _audioTrack == null || opusFrame == null || opusFrame.Length == 0)
                {
                    return;
                }

                if (_peerConnection.connectionState != RTCPeerConnectionState.connected)
                {
                    return;
                }

                byte[]? pcmData = null;
                int samplesDecoded = 0;

                lock (_opusDecoderLock)
                {
                    if (_opusDecoder == null)
                    {
                        try
                        {
                            _opusDecoder = OpusCodecFactory.CreateDecoder(_audioSampleRate, _audioChannels);
                            _logger.LogInformation("✅ Opus 解码器已初始化: {SampleRate}Hz, {Channels} 声道 (使用 OpusCodecFactory)",
                                _audioSampleRate, _audioChannels);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 初始化 Opus 解码器失败");
                            SendAudioOpusDirect(opusFrame);
                            return;
                        }
                    }

                    float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);

                    if (samplesDecoded > 0)
                    {
                        short[] pcmBuffer = new short[samplesDecoded * _audioChannels];
                        for (int i = 0; i < samplesDecoded * _audioChannels; i++)
                        {
                            float clamped = Math.Max(-1.0f, Math.Min(1.0f, pcmBufferFloat[i]));
                            pcmBuffer[i] = (short)(clamped * 32767.0f);
                        }
                        pcmData = new byte[samplesDecoded * _audioChannels * 2];
                        Buffer.BlockCopy(pcmBuffer, 0, pcmData, 0, pcmData.Length);
                    }
                    else
                    {
                        if (_audioPacketCount < 5)
                        {
                            _logger.LogWarning("⚠️ Opus 解码返回 0 个样本");
                        }
                        return;
                    }
                }

                if (pcmData != null && pcmData.Length > 0)
                {
                    SendAudioPCMToWebRTC(pcmData, samplesDecoded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 PCM 失败");
            }
        }
        
        private void SendAudioOpusFallback(byte[] opusFrame)
        {
            SendAudioOpusDirect(opusFrame);
        }

        /// <summary>
        /// 尝试使用 Opus 编码器重新编码并发送（即使浏览器选择了 PCMU，也发送 Opus 以获得高质量）
        /// </summary>
        private bool TrySendOpusReencoded(byte[] originalOpusFrame)
        {
            try
            {
                // ✅ 优化策略：即使浏览器选择了 PCMU，也尝试直接发送原始 Opus
                // 现代浏览器的 WebRTC 实现通常能处理 Opus，即使 SDP 中也选择了 PCMU 作为备用
                // 这样可以获得最高音质，而无需降采样到 8kHz
                
                if (_peerConnection == null || originalOpusFrame == null || originalOpusFrame.Length == 0)
                {
                    return false;
                }
                
                // 直接发送原始 Opus 数据（保持 48kHz 高质量）
                SendAudioOpusDirect(originalOpusFrame);
                
                if (_audioPacketCount < 10)
                {
                    _logger.LogInformation("✅ 即使浏览器选择了 PCMU，也发送 Opus 以获得高质量音质");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                if (_audioPacketCount < 10)
                {
                    _logger.LogWarning(ex, "⚠️ Opus 重新编码失败，将使用转码方案");
                }
                return false;
            }
        }
        
        /// <summary>
        /// 直接发送 Opus 数据（直接发送 Opus RTP 包，不转码）
        /// </summary>
        private bool TrySendOpusDownmixedToStereo(byte[] opusFrame, out DownmixedOpusFrame downmixedFrame)
        {
            downmixedFrame = default;
            
            try
            {
                if (opusFrame == null || opusFrame.Length == 0)
                {
                    return false;
                }

                if (_audioFrameSize <= 0 || _audioSampleRate <= 0 || _audioChannels <= 0)
                {
                    return false;
                }

                float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                int samplesDecoded;

                lock (_opusDecoderLock)
                {
                    if (_opusDecoder == null)
                    {
                        _opusDecoder = OpusCodecFactory.CreateDecoder(_audioSampleRate, _audioChannels);
                        _logger.LogInformation("✅ 下混音频：初始化 Opus 解码器 {Rate}Hz / {Channels}ch", _audioSampleRate, _audioChannels);
                    }

                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);
                }

                if (samplesDecoded <= 0)
                {
                    if (_audioPacketCount < 5)
                    {
                        _logger.LogWarning("⚠️ 下混音频：解码返回 0 个样本");
                    }
                    return false;
                }

                int stereoSamples = samplesDecoded;
                short[] stereoSamplesBuffer = ArrayPool<short>.Shared.Rent(stereoSamples * 2);

                try
                {
                    var stereoSpan = stereoSamplesBuffer.AsSpan(0, stereoSamples * 2);
                    if (!TryBuildStereoSamples(pcmBufferFloat, stereoSamples, _audioChannels, stereoSpan))
                    {
                        if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 下混音频：声道矩阵无效（channels={Channels}），放弃下混", _audioChannels);
                        }
                        return false;
                    }

                    byte[] encodeBuffer = ArrayPool<byte>.Shared.Rent(_opusEncodeBuffer.Length);

                    try
                    {
                        int encodedBytes;
                        lock (_opusEncoderLock)
                        {
                            if (_stereoOpusEncoder == null || _stereoEncoderSampleRate != _audioSampleRate)
                            {
                                _stereoOpusEncoder?.Dispose();
                                _stereoOpusEncoder = new OpusEncoder(_audioSampleRate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
                                _stereoEncoderSampleRate = _audioSampleRate;
                                _stereoOpusEncoder.Bitrate = Math.Min(256000, _audioSampleRate * 4);
                                _logger.LogInformation("✅ 下混音频：初始化立体声 Opus 编码器 {Rate}Hz / 2ch", _audioSampleRate);
                            }

                            encodedBytes = _stereoOpusEncoder.Encode(stereoSamplesBuffer, 0, stereoSamples, encodeBuffer, 0, encodeBuffer.Length);
                        }

                        if (encodedBytes <= 0)
                        {
                            if (_audioPacketCount < 5)
                            {
                                _logger.LogWarning("⚠️ 下混音频：Opus 编码失败，返回 {Bytes} 字节", encodedBytes);
                            }
                            return false;
                        }

                        var downmixedData = new byte[encodedBytes];
                        Buffer.BlockCopy(encodeBuffer, 0, downmixedData, 0, encodedBytes);
                        downmixedFrame = new DownmixedOpusFrame(downmixedData, stereoSamples);
                        return true;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(encodeBuffer);
                    }
                }
                finally
                {
                    ArrayPool<short>.Shared.Return(stereoSamplesBuffer);
                }
            }
            catch (Exception ex)
            {
                if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                {
                    _logger.LogWarning(ex, "⚠️ 下混音频失败，将回退发送原始音频");
                }
                downmixedFrame = default;
                return false;
            }
        }
        
        private bool TryBuildStereoSamples(float[] source, int samples, int sourceChannels, Span<short> destination)
        {
            if (destination.Length < samples * 2)
            {
                return false;
            }

            if (sourceChannels <= 0 || samples <= 0)
            {
                return false;
            }

            if (sourceChannels == 1)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    float value = Math.Clamp(source[sample], -1f, 1f);
                    short converted = (short)Math.Round(value * 32767f);
                    destination[sample * 2] = converted;
                    destination[sample * 2 + 1] = converted;
                }
                return true;
            }

            var matrix = BuildDownmixMatrix(sourceChannels);
            if (!matrix.IsValid || matrix.Left.Length != sourceChannels || matrix.Right.Length != sourceChannels)
            {
                return false;
            }

            var floatSpan = source.AsSpan();
            var leftWeights = matrix.Left;
            var rightWeights = matrix.Right;
            float normalization = matrix.Normalization;

            for (int sample = 0; sample < samples; sample++)
            {
                int baseIndex = sample * sourceChannels;
                float leftValue = 0f;
                float rightValue = 0f;

                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    float value = floatSpan[baseIndex + ch];
                    leftValue += value * leftWeights[ch];
                    rightValue += value * rightWeights[ch];
                }

                leftValue *= normalization;
                rightValue *= normalization;

                float peak = Math.Max(Math.Abs(leftValue), Math.Abs(rightValue));
                if (peak > 1f)
                {
                    float scale = 1f / peak;
                    leftValue *= scale;
                    rightValue *= scale;
                }

                leftValue = Math.Clamp(leftValue, -1f, 1f);
                rightValue = Math.Clamp(rightValue, -1f, 1f);

                destination[sample * 2] = (short)Math.Round(leftValue * 32767f);
                destination[sample * 2 + 1] = (short)Math.Round(rightValue * 32767f);
            }

            return true;
        }

        private static int ParseAudioChannels(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 2)
            {
                int be = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0, 2));
                if (IsValidChannelCount(be)) return be;

                int le = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
                if (IsValidChannelCount(le)) return le;
            }

            if (span.Length >= 1 && IsValidChannelCount(span[0]))
            {
                return span[0];
            }

            return 2;
        }

        private static int ParseBitsPerSample(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 8)
            {
                int be = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6, 2));
                if (IsValidBitsPerSample(be)) return be;

                int le = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
                if (IsValidBitsPerSample(le)) return le;
            }

            if (span.Length > 6 && IsValidBitsPerSample(span[6]))
            {
                return span[6];
            }

            return 16;
        }

        private static int ParseSampleRate(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 6)
            {
                int be = BinaryPrimitives.ReadInt32BigEndian(span.Slice(2, 4));
                if (IsValidSampleRate(be)) return be;

                int le = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(2, 4));
                if (IsValidSampleRate(le)) return le;
            }

            return 48000;
        }

        private static int ParseFrameSize(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 12)
            {
                int be32 = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
                if (IsValidFrameSize(be32)) return be32;

                int le32 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
                if (IsValidFrameSize(le32)) return le32;
            }

            if (span.Length >= 10)
            {
                int be16 = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8, 2));
                if (IsValidFrameSize(be16)) return be16;

                int le16 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
                if (IsValidFrameSize(le16)) return le16;
            }

            return 480;
        }

        private static bool IsValidChannelCount(int value) => value >= 1 && value <= 8;

        private static bool IsValidBitsPerSample(int value) => value is 8 or 16 or 24 or 32;

        private static bool IsValidSampleRate(int value) => value >= 8000 && value <= 192000;

        private static bool IsValidFrameSize(int value) => value >= 60 && value <= 8192;

        private readonly struct DownmixedOpusFrame
        {
            public DownmixedOpusFrame(byte[] frameData, int samplesPerFrame)
            {
                FrameData = frameData;
                SamplesPerFrame = samplesPerFrame;
            }

            public byte[] FrameData { get; }
            public int SamplesPerFrame { get; }
            public bool IsValid => FrameData != null && FrameData.Length > 0 && SamplesPerFrame > 0;
        }

        private readonly struct DownmixMatrix
        {
            public DownmixMatrix(float[] left, float[] right, float normalization)
            {
                Left = left;
                Right = right;
                Normalization = normalization;
            }

            public float[] Left { get; }
            public float[] Right { get; }
            public float Normalization { get; }
            public bool IsValid => Left.Length > 0 && Right.Length > 0;
        }

        private static DownmixMatrix BuildDownmixMatrix(int channels)
        {
            if (channels <= 0)
            {
                return new DownmixMatrix(Array.Empty<float>(), Array.Empty<float>(), 1f);
            }

            const float INV_SQRT2 = 0.70710677f; // ≈ 1/√2
            const float LFE_GAIN = 0.5f;
            const float SURROUND_GAIN = 0.70710677f;
            const float DIRECT_GAIN = 1f;

            var left = new float[channels];
            var right = new float[channels];

            switch (channels)
            {
                case 1: // Mono
                    left[0] = DIRECT_GAIN;
                    right[0] = DIRECT_GAIN;
                    break;
                case 2: // Stereo
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    break;
                case 3: // L, R, C
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    break;
                case 4: // L, R, Ls, Rs
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = SURROUND_GAIN;
                    right[3] = SURROUND_GAIN;
                    break;
                case 5: // L, R, C, Ls, Rs
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = SURROUND_GAIN;
                    right[4] = SURROUND_GAIN;
                    break;
                case 6: // 5.1 -> L, R, C, LFE, Ls, Rs
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    left[4] = SURROUND_GAIN;
                    right[5] = SURROUND_GAIN;
                    break;
                case 7: // 6.1 -> L, R, C, LFE, Ls, Rs, Cs
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    left[4] = SURROUND_GAIN;
                    right[5] = SURROUND_GAIN;
                    left[6] = SURROUND_GAIN;
                    right[6] = SURROUND_GAIN;
                    break;
                default: // 7.1 及以上 -> L, R, C, LFE, Ls, Rs, Lb, Rb, ...
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    if (channels > 4)
                    {
                        left[4] = SURROUND_GAIN;
                    }
                    if (channels > 5)
                    {
                        right[5] = SURROUND_GAIN;
                    }
                    if (channels > 6)
                    {
                        left[6] = SURROUND_GAIN;
                    }
                    if (channels > 7)
                    {
                        right[7] = SURROUND_GAIN;
                    }
                    for (int ch = 8; ch < channels; ch++)
                    {
                        if ((ch & 1) == 0)
                        {
                            left[ch] = SURROUND_GAIN;
                        }
                        else
                        {
                            right[ch] = SURROUND_GAIN;
                        }
                    }
                    break;
            }

            float sumLeft = 0f;
            float sumRight = 0f;
            for (int i = 0; i < channels; i++)
            {
                sumLeft += Math.Abs(left[i]);
                sumRight += Math.Abs(right[i]);
            }

            float maxSum = Math.Max(sumLeft, sumRight);
            float normalization = maxSum > 1f ? 1f / maxSum : 1f;

            return new DownmixMatrix(left, right, normalization);
        }

        private void SendAudioOpusDirect(byte[] opusFrame, int? samplesPerFrameOverride = null)
        {
            try
            {
                if (_peerConnection == null || opusFrame == null || opusFrame.Length == 0)
                {
                    return;
                }
                var connectionState = _peerConnection.connectionState;
                var iceState = _peerConnection.iceConnectionState;
                var signalingState = _peerConnection.signalingState;
                // 允许在以下情况下发送：
                // 1. 信令状态是 stable（正常情况）
                // 2. 信令状态是 have_local_offer 但 ICE 已连接或正在检查（Answer 可能已设置但状态未更新）
                // 3. 连接状态是 connected 或 connecting
                bool canSend = signalingState == RTCSignalingState.stable ||
                               (signalingState == RTCSignalingState.have_local_offer && 
                                (iceState == RTCIceConnectionState.connected || 
                                 iceState == RTCIceConnectionState.checking ||
                                 connectionState == RTCPeerConnectionState.connected ||
                                 connectionState == RTCPeerConnectionState.connecting));
                
                if (!canSend)
                {
                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                    {
                        _logger.LogDebug("⏳ 等待音频通道就绪: signaling={Signaling}, connection={Connection}, ICE={Ice}", 
                            signalingState, connectionState, iceState);
                    }
                    return;
                }
                
                // ✅ Opus 时间戳：基于 48000Hz 采样率
                // 每帧通常是 480 个样本（10ms @ 48kHz）
                int samplesPerFrame = samplesPerFrameOverride ?? _audioFrameSize; // 通常是 480
                if (samplesPerFrame <= 0)
                {
                    samplesPerFrame = _audioFrameSize > 0 ? _audioFrameSize : 480;
                }
                uint currentTimestamp = _audioTimestamp;
                _audioTimestamp += (uint)samplesPerFrame;
                
                // ✅ 确保序列号正确递增
                ushort currentSeqNum = (ushort)(_audioSequenceNumber & 0xFFFF);
                _audioSequenceNumber++;
                
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + opusFrame.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 111; // OPUS payload type (标准)
                rtpPacket.Header.SequenceNumber = currentSeqNum;
                rtpPacket.Header.Timestamp = currentTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                // 复制 Opus 数据到 payload
                Buffer.BlockCopy(opusFrame, 0, rtpPacket.Payload, 0, opusFrame.Length);
                
                // 尝试发送音频 RTP 包（使用 Opus payload type 111）
                byte[] rtpBytes = rtpPacket.GetBytes();
                
                if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                {
                    _logger.LogDebug("📤 发送 Opus RTP 包: seq={Seq}, ts={Ts}, samples={Samples}, size={Size} bytes", 
                        currentSeqNum, currentTimestamp, samplesPerFrame, opusFrame.Length);
                }
                
                SendAudioRTPRaw(rtpBytes, opusFrame, 111); // 明确指定 Opus payload type
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 Opus 数据失败");
            }
        }
        
        /// <summary>
        /// 发送 PCM 数据到 WebRTC
        /// </summary>
        private void SendAudioPCMToWebRTC(byte[] pcmData, int samplesDecoded)
        {
            try
            {
                // 更新时间戳（基于实际解码的样本数）
                _audioTimestamp += (uint)samplesDecoded;
                
                // ⚠️ 重要：由于 SendAudio 需要音频轨道配置，而 SendRtpRaw 已经成功
                // 直接使用 SendRtpRaw 方式发送，跳过 SendAudio（避免 "missing audio track" 错误）
                // 方法：直接创建 RTP 包并通过 SendRtpRaw 发送
                SendAudioPCMAsRTP(pcmData, samplesDecoded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCM 到 WebRTC 失败");
            }
        }
        
        /// <summary>
        /// 将 PCM 数据打包为 RTP 并发送
        /// </summary>
        private void SendAudioPCMAsRTP(byte[] pcmData, int samplesDecoded)
        {
            try
            {
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + pcmData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 111; // OPUS payload type（虽然数据是 PCM，但使用 OPUS 的 payload type）
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                // 复制 PCM 数据到 payload
                Buffer.BlockCopy(pcmData, 0, rtpPacket.Payload, 0, pcmData.Length);
                
                // 尝试发送 RTP 包（注意：PCM 通常需要编码为 PCMU/PCMA，这里保留原逻辑作为备用）
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmData, 111); // 使用 111 作为备用（实际应该编码为 PCMU）
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCM RTP 包失败");
            }
        }
        
        /// <summary>
        /// 发送原始 RTP 包（通用方法）
        /// </summary>
        private void SendAudioRTPRaw(byte[] rtpBytes, byte[] originalData, int payloadType = 111)
        {
            try
            {
                // ⚠️ 参照视频发送逻辑：优先使用 SendRtpRaw，尝试多种方法签名
                if (_peerConnection == null) return;
                var peerConnectionType = _peerConnection.GetType();
                var sendRtpRawMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name == "SendRtpRaw")
                    .ToList();
                
                // 如果当前类型没有找到，尝试基类
                if (sendRtpRawMethods.Count == 0)
                {
                    var baseType = peerConnectionType.BaseType;
                    if (baseType != null)
                    {
                        sendRtpRawMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Where(m => m.Name == "SendRtpRaw")
                            .ToList();
                    }
                }
                
                bool rtpSent = false;
                foreach (var method in sendRtpRawMethods)
                {
                    try
                    {
                        var parameters = method.GetParameters();
                        
                        // 尝试各种 SendRtpRaw 签名
                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte[]))
                        {
                            if (parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                            {
                                method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.audio });
                                if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                {
                                    _logger.LogDebug("✅ 音频 RTP 包已发送 (2参数, SDPMediaTypesEnum): size={Size}", rtpBytes.Length);
                                }
                                rtpSent = true;
                                break;
                            }
                            else if (parameters[1].ParameterType == typeof(int))
                            {
                                method.Invoke(_peerConnection, new object[] { rtpBytes, payloadType });
                                if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                {
                                    _logger.LogDebug("✅ 音频 RTP 包已发送 (2参数, int): payloadType={Pt}, size={Size}", payloadType, rtpBytes.Length);
                                }
                                rtpSent = true;
                                break;
                            }
                        }
                        else if (parameters.Length == 6 &&
                                 parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                 parameters[1].ParameterType == typeof(byte[]) &&
                                 parameters[2].ParameterType == typeof(uint) &&
                                 parameters[3].ParameterType == typeof(int) &&
                                 parameters[4].ParameterType == typeof(int) &&
                                 parameters[5].ParameterType == typeof(ushort))
                        {
                            // ⚠️ 关键修复：SendRtpRaw 方法签名是：
                            // SendRtpRaw(SDPMediaTypesEnum mediaType, Byte[] payload, UInt32 timestamp, Int32 markerBit, Int32 payloadTypeID, UInt16 seqNum)
                            // 注意：参数是 payload（纯数据），不是完整的 RTP 包！
                            // SIPSorcery 会自己构建 RTP 头
                            
                            // ✅ 关键：从 RTP 包中提取时间戳和序列号，确保与 RTP 头一致
                            // 对于 6 参数版本，需要传入纯 payload，但时间戳和序列号要从 RTP 包中提取
                            byte[] payloadData = originalData;
                            
                            // 从 RTP 包中解析时间戳和序列号
                            uint timestamp = 0;
                            ushort seqNum = 0;
                            if (rtpBytes.Length >= 12)
                            {
                                // RTP 头格式：V(2) P(1) X(1) CC(4) M(1) PT(7) | Sequence(16) | Timestamp(32) | SSRC(32)
                                seqNum = (ushort)((rtpBytes[2] << 8) | rtpBytes[3]);
                                timestamp = (uint)((rtpBytes[4] << 24) | (rtpBytes[5] << 16) | (rtpBytes[6] << 8) | rtpBytes[7]);
                            }
                            else
                            {
                                // 如果 RTP 包格式不正确，使用当前值作为后备
                                seqNum = (ushort)((_audioSequenceNumber - 1) & 0xFFFF);
                                timestamp = _audioTimestamp;
                            }
                            
                            int markerBit = 0; // 音频通常不使用 marker bit
                            
                            try
                            {
                                method.Invoke(_peerConnection, new object[] { 
                                    SDPMediaTypesEnum.audio, 
                                    payloadData, // ⚠️ 传入纯 payload，不是 RTP 包
                                    timestamp, 
                                    markerBit, // marker bit
                                    payloadType, // payload type
                                    seqNum 
                                });
                                
                                // ✅ 发送成功，记录日志
                                if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                {
                                    _logger.LogDebug("✅ 音频 RTP 包已发送 (6参数): seq={Seq}, ts={Ts}, payloadType={Pt}, size={Size}", 
                                        seqNum, timestamp, payloadType, payloadData.Length);
                                }
                            }
                            catch (Exception invokeEx)
                            {
                                // ⚠️ 捕获内部异常，记录详细信息
                                var innerEx = invokeEx.InnerException ?? invokeEx;
                                _logger.LogError(innerEx, "❌ SendRtpRaw (6参数) 调用异常: seqNum={Seq}, timestamp={Ts}, payloadType={Pt}, payloadLen={Len}, error={Error}", 
                                    seqNum, timestamp, payloadType, payloadData.Length, innerEx.Message);
                                throw; // 重新抛出，让外层 catch 处理
                            }
                            rtpSent = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                        {
                            var innerEx = ex.InnerException ?? ex;
                            _logger.LogWarning("⚠️ SendRtpRaw 调用失败: {Ex}, 方法: {Method}", 
                                innerEx.Message, 
                                string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)));
                        }
                    }
                }
                
                if (!rtpSent)
                {
                    if (_audioPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 音频 RTP 包已构建但未发送: seq={Seq}, size={Size}, 找到方法数: {Count}", 
                            _audioSequenceNumber, rtpBytes.Length, sendRtpRawMethods.Count);
                        
                        if (sendRtpRawMethods.Count == 0)
                        {
                            _logger.LogWarning("⚠️ 未找到 SendRtpRaw 方法，检查连接状态: {State}, ICE: {Ice}, 信令: {Signaling}", 
                                _peerConnection?.connectionState, 
                                _peerConnection?.iceConnectionState, 
                                _peerConnection?.signalingState);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 RTP 包失败");
            }
        }
        
        /// <summary>
        /// 旧的音频发送方法（保留作为参考，但已不再使用）
        /// </summary>
        [Obsolete("使用 SendAudioPCM 替代")]
        private void SendAudioRTP(byte[] data)
        {
            try
            {
                if (_peerConnection == null || _audioTrack == null)
                {
                    if (_audioPacketCount < 5)
                    {
                        _logger.LogDebug("⏳ 等待音频 RTP 通道就绪...");
                    }
                    return;
                }
                
                if (_peerConnection.connectionState != RTCPeerConnectionState.connected)
                {
                    return;
                }
                
                // ⚠️ 注意：当前 PlayStation Remote Play 使用 AAC 音频
                // WebRTC 通常需要 OPUS，但某些浏览器也支持 AAC
                // 这里先尝试直接发送 AAC 数据，如果不行则需要转码
                
                // 更新时间戳（每帧递增）
                // 假设每帧 480 个样本（10ms @ 48kHz）
                int samplesPerFrame = 480;
                _audioTimestamp += (uint)samplesPerFrame;
                
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + data.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 111; // OPUS payload type（或使用 AAC 的 97）
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                // 复制音频数据到 payload
                Buffer.BlockCopy(data, 0, rtpPacket.Payload, 0, data.Length);
                
                // 尝试发送音频 RTP 包
                try
                {
                    byte[] rtpBytes = rtpPacket.GetBytes();
                if (_audioPacketCount % 100 == 0)
                {
                        _logger.LogInformation("📤 准备发送音频 RTP 包: seq={Seq}, ts={Ts}, size={Size} bytes", 
                            rtpPacket.Header.SequenceNumber, rtpPacket.Header.Timestamp, rtpBytes.Length);
                    }
                    
                    // 尝试发送音频 RTP 包（使用反射调用 SendAudio 或 SendRtpRaw）
                    try
                    {
                        // 方法1：尝试 SendAudio
                        var sendAudioMethods = _peerConnection.GetType().GetMethods()
                            .Where(m => m.Name == "SendAudio")
                            .ToList();
                        
                        foreach (var method in sendAudioMethods)
                        {
                            try
                            {
                                var parameters = method.GetParameters();
                                if (parameters.Length == 3 && 
                                    parameters[0].ParameterType == typeof(uint) &&
                                    parameters[1].ParameterType == typeof(int) &&
                                    parameters[2].ParameterType == typeof(byte[]))
                                {
                                    method.Invoke(_peerConnection, new object[] { _audioTimestamp, 111, data });
                                    if (_audioPacketCount % 100 == 0)
                                    {
                                        _logger.LogInformation("✅ 音频数据已通过 SendAudio 发送: seq={Seq}", rtpPacket.Header.SequenceNumber);
                                    }
                                    return; // 发送成功
                                }
                            }
                            catch { }
                        }
                        
                        // 方法2：尝试 SendRtpRaw
                        var sendRtpRawMethods = _peerConnection.GetType().GetMethods()
                            .Where(m => m.Name == "SendRtpRaw" && m.GetParameters().Length == 2)
                            .ToList();
                        
                        foreach (var method in sendRtpRawMethods)
                        {
                            try
                            {
                                var parameters = method.GetParameters();
                                if (parameters[0].ParameterType == typeof(byte[]))
                                {
                                    if (parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                                    {
                                        method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.audio });
                                        if (_audioPacketCount % 100 == 0)
                                        {
                                            _logger.LogDebug("✅ 音频 RTP 包已通过 SendRtpRaw 发送: seq={Seq}", rtpPacket.Header.SequenceNumber);
                                        }
                                        return; // 发送成功
                                    }
                                    else if (parameters[1].ParameterType == typeof(int))
                                    {
                                        method.Invoke(_peerConnection, new object[] { rtpBytes, 111 });
                                        if (_audioPacketCount % 100 == 0)
                                        {
                                            _logger.LogDebug("✅ 音频 RTP 包已通过 SendRtpRaw(byte[], int) 发送: seq={Seq}", rtpPacket.Header.SequenceNumber);
                                        }
                                        return; // 发送成功
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("发送音频 RTP 包异常: {Ex}", ex.Message);
                    }
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "❌ 发送音频 RTP 包失败");
                }
                
                _audioPacketCount++;
                if (_audioPacketCount <= 3 || _audioPacketCount % 1000 == 0)
                {
                    _logger.LogDebug("🔊 音频包已构建: {Count}, size: {Size} bytes", _audioPacketCount, data.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 RTP 包失败");
            }
        }
        
        /// <summary>
        /// 初始化反射方法缓存（性能优化：避免每次发送时查找方法）
        /// </summary>
        private void InitializeReflectionMethods()
        {
            lock (_methodsLock)
            {
                if (_methodsInitialized || _peerConnection == null)
                    return;
                
                try
                {
                    var peerConnectionType = _peerConnection.GetType();
                    
                    // 查找 SendVideo(uint, byte[]) 方法
                    var sendVideoMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name == "SendVideo")
                        .ToList();
                    
                    if (sendVideoMethods.Count == 0)
                    {
                        var baseType = peerConnectionType.BaseType;
                        if (baseType != null)
                        {
                            sendVideoMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name == "SendVideo")
                                .ToList();
                        }
                    }
                    
                    foreach (var method in sendVideoMethods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType == typeof(uint) &&
                            parameters[1].ParameterType == typeof(byte[]))
                        {
                            _cachedSendVideoMethod = method;
                            break;
                        }
                    }
                    
                    // 查找 SendRtpRaw 相关方法（用于视频和音频）
                    var sendRtpRawMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name == "SendRtpRaw" || m.Name == "SendRtpPacket")
                        .ToList();
                    
                    if (sendRtpRawMethods.Count == 0)
                    {
                        var baseType = peerConnectionType.BaseType;
                        if (baseType != null)
                        {
                            sendRtpRawMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name == "SendRtpRaw" || m.Name == "SendRtpPacket")
                                .ToList();
                        }
                    }
                    
                    // 查找 SendRtpRaw(byte[], SDPMediaTypesEnum) 或 SendRtpRaw(byte[], int)
                    foreach (var method in sendRtpRawMethods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte[]))
                        {
                            if (parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                            {
                                _cachedSendRtpRawVideoMethod = method;
                            }
                            else if (parameters[1].ParameterType == typeof(int))
                            {
                                _cachedSendRtpRawAudioMethod = method;
                            }
                        }
                        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte[]))
                        {
                            _cachedSendRtpRawMethod = method;
                        }
                    }
                    
                    _methodsInitialized = true;
                    _logger.LogDebug("✅ 反射方法缓存初始化完成: SendVideo={HasSendVideo}, SendRtpRaw={HasRtpRaw}", 
                        _cachedSendVideoMethod != null, _cachedSendRtpRawMethod != null || _cachedSendRtpRawVideoMethod != null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ 初始化反射方法缓存失败，将使用运行时查找");
                }
            }
        }
        
        /// <summary>
        /// 获取缓存的连接状态（性能优化：减少属性访问）
        /// </summary>
        private (RTCPeerConnectionState connectionState, RTCIceConnectionState iceState, RTCSignalingState signalingState) GetCachedConnectionState()
        {
            var now = DateTime.UtcNow;
            if (_cachedConnectionState.HasValue && 
                _cachedIceState.HasValue && 
                _cachedSignalingState.HasValue &&
                (now - _lastStateCheckTime).TotalMilliseconds < STATE_CACHE_MS)
            {
                // 使用缓存的状态
                return (_cachedConnectionState.Value, _cachedIceState.Value, _cachedSignalingState.Value);
            }
            
            // 更新缓存
            if (_peerConnection != null)
            {
                _cachedConnectionState = _peerConnection.connectionState;
                _cachedIceState = _peerConnection.iceConnectionState;
                _cachedSignalingState = _peerConnection.signalingState;
                _lastStateCheckTime = now;
                return (_cachedConnectionState.Value, _cachedIceState.Value, _cachedSignalingState.Value);
            }
            
            // 如果 peerConnection 为 null，返回默认值（正常情况下不应该发生）
            // RTCSignalingState 没有 @new 值，使用 stable 作为默认值
            return (RTCPeerConnectionState.@new, RTCIceConnectionState.@new, RTCSignalingState.stable);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _logger.LogInformation("🛑 WebRTCReceiver 正在释放资源 - 视频包: {Video}, 音频包: {Audio}", 
                _videoPacketCount, _audioPacketCount);
            
            try
            {
                // 清理 Opus 解码器（参照 FfmpegMuxReceiver）
                lock (_opusDecoderLock)
                {
                    _opusDecoder?.Dispose();
                    _opusDecoder = null;
                }
                
                lock (_opusEncoderLock)
                {
                    _stereoOpusEncoder?.Dispose();
                    _stereoOpusEncoder = null;
                }

                lock (_rtcpFeedbackLock)
                {
                    foreach (var subscription in _rtcpFeedbackSubscriptions)
                    {
                        try
                        {
                            subscription.@event.RemoveEventHandler(subscription.target, subscription.handler);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "⚠️ 移除 RTCP 反馈事件处理程序失败: {Event}", subscription.@event.Name);
                        }
                    }

                    _rtcpFeedbackSubscriptions.Clear();
                    _rtcpSubscribedEventKeys.Clear();
                }
                
                // ✅ 使用超时机制释放 WebRTC 连接，避免阻塞太久
                if (_peerConnection != null)
                {
                    try
                    {
                        var disposeTask = Task.Run(() =>
                        {
                            _peerConnection.close();
                            _peerConnection.Dispose();
                        });
                        var timeoutTask = Task.Delay(1000); // 最多等待 1 秒
                        var completedTask = Task.WhenAny(disposeTask, timeoutTask).GetAwaiter().GetResult();
                        
                        if (completedTask == timeoutTask)
                        {
                            _logger.LogWarning("⚠️ WebRTC 连接释放超时（1秒），强制继续");
                        }
                        else
                        {
                            _logger.LogDebug("✅ WebRTC 连接已释放");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 释放 WebRTC 连接时发生异常");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 释放 WebRTC 连接失败");
            }
        }
    }
    
    /// <summary>
    /// 简单的视频编码器端点（占位）
    /// </summary>
    internal class VideoEncoderEndpoint
    {
        // 在实际应用中，这里会处理视频编码
        // 对于 PlayStation Remote Play，视频已经是 H.264 编码，可以直接传输
    }
    
    /// <summary>
    /// 简单的音频编码器端点（占位）
    /// </summary>
    internal class AudioEncoderEndpoint
    {
        // 在实际应用中，这里会处理音频编码
        // 可能需要将 AAC 转换为 OPUS
    }
}

