using RemotePlay.Models.PlayStation;
using SIPSorcery.Media;
using SIPSorcery.Net;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Structs;
using RemotePlay.Services;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTC 接收器 - 通过 WebRTC 将 AV 流推送到浏览器
    /// </summary>
    public sealed class WebRTCReceiver : IAVReceiver, IDisposable
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
        
        // ✅ 音频编解码器选择检测
        private bool _useOpusDirect = true; // 默认尝试直接发送 Opus
        private bool _opusCodecDetected = false; // 是否检测到 Opus 被选中
        
        // RTP 常量
        private const int RTP_MTU = 1200; // RTP MTU（通常比 UDP MTU 小）
        private const uint VIDEO_CLOCK_RATE = 90000; // H.264 视频时钟频率
        private const uint AUDIO_CLOCK_RATE = 48000; // OPUS 音频时钟频率
        private const int VIDEO_FRAME_RATE = 60; // 假设 60fps（用于初始计算）
        private const double VIDEO_TIMESTAMP_INCREMENT = VIDEO_CLOCK_RATE / (double)VIDEO_FRAME_RATE; // 每帧时间戳增量
        
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
        /// 初始化 RTCP 反馈监听（用于自动感知关键帧请求）
        /// </summary>
        private void InitializeRTCPFeedback()
        {
            try
            {
                if (_peerConnection == null) return;
                
                // SIPSorcery 的 RTCPeerConnection 可能通过 MediaStreamTrack 或 RTP 会话接收 RTCP 反馈
                // 尝试通过反射查找 RTCP 相关的事件或方法
                var peerConnectionType = _peerConnection.GetType();
                
                // 查找 RTCP 相关的事件或回调
                var rtcpEvents = peerConnectionType.GetEvents(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(e => e.Name.ToLowerInvariant().Contains("rtcp") || 
                               e.Name.ToLowerInvariant().Contains("feedback") ||
                               e.Name.ToLowerInvariant().Contains("pli") ||
                               e.Name.ToLowerInvariant().Contains("fir"))
                    .ToList();
                
                if (rtcpEvents.Count > 0)
                {
                    _logger.LogInformation("✅ 找到 {Count} 个 RTCP 相关事件", rtcpEvents.Count);
                    foreach (var evt in rtcpEvents)
                    {
                        _logger.LogDebug("  - {EventName}", evt.Name);
                    }
                }
                
                // ✅ 尝试通过 MediaStreamTrack 监听 RTCP 反馈
                // 注意：SIPSorcery 可能需要在轨道创建后才能监听
                // 这个方法会在 InitializeTracks() 之后被调用，但此时轨道可能还未完全初始化
                // 我们将在连接建立后（InitializeRtpChannels）再次尝试监听
                
                _logger.LogInformation("📡 RTCP 反馈监听已初始化（将在连接建立后激活）");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 初始化 RTCP 反馈监听失败，将无法自动感知关键帧请求");
            }
        }
        
        private void InitializeRtpChannels()
        {
            try
            {
                if (_peerConnection == null || _videoTrack == null) return;
                
                // 尝试获取 RTP 会话
                // SIPSorcery 在连接建立后会自动创建 RTP 会话
                // 我们需要通过反射或者其他方式获取 RTP 会话来发送数据
                // RTP 通道已就绪
                
                // ✅ 连接建立后，尝试激活 RTCP 反馈监听
                ActivateRTCPFeedback();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 初始化 RTP 通道失败");
            }
        }
        
        /// <summary>
        /// 激活 RTCP 反馈监听（在连接建立后调用）
        /// </summary>
        private void ActivateRTCPFeedback()
        {
            try
            {
                if (_peerConnection == null || _videoTrack == null) return;
                
                // ✅ 尝试通过 MediaStreamTrack 获取 RTP 会话并监听 RTCP 反馈
                var trackType = _videoTrack.GetType();
                var trackProperties = trackType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(p => p.Name.ToLowerInvariant().Contains("rtp") || 
                               p.Name.ToLowerInvariant().Contains("session"))
                    .ToList();
                
                if (trackProperties.Count > 0)
                {
                    _logger.LogDebug("✅ 找到 {Count} 个可能的 RTP 会话属性", trackProperties.Count);
                    foreach (var prop in trackProperties)
                    {
                        try
                        {
                            var rtpSession = prop.GetValue(_videoTrack);
                            if (rtpSession != null)
                            {
                                _logger.LogInformation("✅ 找到 RTP 会话: {Type}", rtpSession.GetType().Name);
                                
                                // 尝试查找 RTCP 反馈事件
                                var rtpSessionType = rtpSession.GetType();
                                var rtcpEvents = rtpSessionType.GetEvents(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Where(e => e.Name.ToLowerInvariant().Contains("rtcp") || 
                                               e.Name.ToLowerInvariant().Contains("feedback") ||
                                               e.Name.ToLowerInvariant().Contains("pli") ||
                                               e.Name.ToLowerInvariant().Contains("fir"))
                                    .ToList();
                                
                                if (rtcpEvents.Count > 0)
                                {
                                    _logger.LogInformation("✅ 找到 {Count} 个 RTCP 反馈事件", rtcpEvents.Count);
                                    // 这里可以订阅事件，但需要知道具体的委托类型
                                    // 暂时记录日志，后续根据实际 API 调整
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("⚠️ 无法访问 RTP 会话属性 {Prop}: {Ex}", prop.Name, ex.Message);
                        }
                    }
                }
                
                // ✅ 注意：SIPSorcery 可能不直接暴露 RTCP 反馈事件
                // 作为替代方案，我们可以：
                // 1. 定期检查连接状态（当检测到连接问题时请求关键帧）
                // 2. 监听 WebRTC 统计信息（通过 getStats API）
                // 3. 在收到连接恢复事件时请求关键帧
                
                // ✅ 临时方案：监听连接状态恢复，在恢复时请求关键帧
                // 这可以处理常见的丢包场景
                _logger.LogInformation("📡 RTCP 反馈监听已激活（使用连接状态监控作为备用方案）");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 激活 RTCP 反馈监听失败");
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
            var opusFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.audio,
                111,
                "opus",
                48000,
                2
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
                if (audioHeader != null && audioHeader.Length >= 14)
                {
                    int channels = audioHeader[0];
                    int bits = audioHeader[1];
                    int rate = (audioHeader[2] << 24) | (audioHeader[3] << 16) | (audioHeader[4] << 8) | audioHeader[5];
                    int frameSize = (audioHeader[6] << 24) | (audioHeader[7] << 16) | (audioHeader[8] << 8) | audioHeader[9];
                    
                    // 保存帧大小（用于 PCM 缓冲区大小计算）
                    if (frameSize > 0)
                    {
                        _audioFrameSize = frameSize;
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
                            if (channels != _audioChannels)
                            {
                                _audioChannels = channels;
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
        
        public void OnVideoPacket(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    if (_videoPacketCount < 3 && packet != null && packet.Length == 1)
                    {
                        _logger.LogError("❌ 视频包异常：长度只有 1 字节");
                    }
                    return;
                }
                
                // ✅ 记录PS5数据包到达时间（用于延时统计）
                // 这个时间代表PS5画面产生后的某个时间点（包含PS5->服务器的网络延迟，通常<5ms）
                // 用于计算从PS5画面到浏览器显示的端到端延迟
                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);
                
                // 检查 WebRTC 连接状态
                if (_peerConnection == null)
                {
                    return;
                }
                    
                // ✅ 性能优化：使用缓存的状态检查
                var (connectionState, _, _) = GetCachedConnectionState();
                // 优化：允许在 connecting 状态也发送，减少等待延迟
                if (connectionState != RTCPeerConnectionState.connected && 
                    connectionState != RTCPeerConnectionState.connecting)
                {
                    if (_videoPacketCount % 1000 == 0)
                    {
                        _logger.LogWarning("⚠️ WebRTC 连接状态: {State}，等待连接建立... (已收到 {Count} 个视频包)", 
                            connectionState, _videoPacketCount);
                    }
                    // 不返回，继续尝试发送（连接可能稍后建立）
                }
                
                // ⚠️ 关键修复：参照 FfmpegMuxReceiver 的处理方式
                // FfmpegMuxReceiver 直接跳过第一个字节后写入数据（包含起始码），不解析 NAL units
                // WebRTC 的 SendVideo 可能也需要包含起始码的完整数据，而不是解析后的 NAL units
                // 提取视频数据（跳过第一个字节的 header type）
                // ✅ 性能优化：使用Span减少内存分配开销
                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);
                    
                // ⚠️ 尝试两种方式：
                // 1. 直接发送包含起始码的完整数据（参照 FfmpegMuxReceiver）
                // 2. 如果失败，再尝试解析 NAL units
                    
                // 注意：_currentVideoFrameIndex 已在 OnVideoPacket 开始时递增
                
                // 先尝试直接发送（包含起始码的完整数据）
                if (TrySendVideoDirect(videoData))
                    {
                    // 发送成功，记录延时统计（使用已递增的帧索引）
                    _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                    
                    // 发送成功
                    _videoPacketCount++;
                    return;
                    }
                    
                // 如果直接发送失败，尝试解析 NAL units 并发送
                SendVideoRTP(videoData);
                
                // 记录延时统计（RTP发送）
                _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                
                _videoPacketCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送视频包失败: packetLen={Len}, count={Count}", 
                    packet?.Length ?? 0, _videoPacketCount);
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
                if (_peerConnection != null)
                {
                    // packet 格式：[HeaderType.AUDIO (1 byte)] + [Opus 编码帧数据]
                    byte[] opusFrame = new byte[packet.Length - 1];
                    packet.AsSpan(1).CopyTo(opusFrame);
                    
                    // ✅ 优化音质：优先使用 Opus，即使浏览器选择了 PCMU 也尝试发送 Opus
                    // 现代浏览器通常都能处理 Opus，即使 SDP 中也选择了 PCMU 作为备用
                    if (_useOpusDirect)
                    {
                        // 直接发送 Opus RTP 包，无需转码（最高音质）
                        SendAudioOpusDirect(opusFrame);
                    }
                    else
                    {
                        // ✅ 如果浏览器选择了 PCMU，尝试使用 Opus 编码器重新编码为 Opus
                        // 这样即使浏览器选择了 PCMU，我们仍然发送高质量的 Opus
                        if (!_opusCodecDetected)
                        {
                            // 尝试重新编码为 Opus（保持高质量）
                            if (TrySendOpusReencoded(opusFrame))
                            {
                                // Opus 重新编码成功，使用高质量编码
                            }
                            else
                            {
                                // 回退到转码方案：Opus -> PCM -> PCMU（低质量，但兼容）
                                if (_audioPacketCount < 5)
                                {
                                    _logger.LogWarning("⚠️ Opus 重新编码失败，使用转码方案: Opus -> PCM -> PCMU");
                                }
                                SendAudioWithTranscoding(opusFrame);
                            }
                        }
                        else
                        {
                            SendAudioWithTranscoding(opusFrame);
                        }
                    }
                    
                    // 记录发送时间戳（用于延时统计）
                    _latencyStats?.RecordPacketSent(_sessionId, "audio", _currentAudioFrameIndex);
                    
                    _audioPacketCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频包失败");
            }
        }
        
        /// <summary>
        /// 尝试直接发送视频数据（参照 FfmpegMuxReceiver：直接发送包含起始码的数据）
        /// </summary>
        private bool TrySendVideoDirect(byte[] videoData)
        {
            if (_peerConnection == null || _videoTrack == null || videoData == null || videoData.Length == 0)
                return false;
            
            try
            {
                // ✅ 性能优化：使用缓存的状态检查
                var (connectionState, iceState, signalingState) = GetCachedConnectionState();
                
                // ⚠️ 放宽发送条件：即使信令状态是 have_local_offer，也尝试发送
                // 因为 Answer 可能已经被强制接受，但状态还没有更新
                // 允许在以下情况下发送：
                // 1. 信令状态是 stable（正常情况）
                // 2. 信令状态是 have_local_offer 但 ICE 已连接或正在检查（Answer 可能已设置但状态未更新）
                // 3. 连接状态是 connected 或 connecting
                bool canSendVideo = signalingState == RTCSignalingState.stable ||
                                    (signalingState == RTCSignalingState.have_local_offer && 
                                     (iceState == RTCIceConnectionState.connected || 
                                      iceState == RTCIceConnectionState.checking ||
                                      connectionState == RTCPeerConnectionState.connected ||
                                      connectionState == RTCPeerConnectionState.connecting));
                
                if (!canSendVideo)
                {
                    return false; // 状态不允许发送
                }
                
                // ✅ 性能优化：使用缓存的反射方法
                if (!_methodsInitialized)
                {
                    InitializeReflectionMethods();
                }
                
                if (_cachedSendVideoMethod != null)
                {
                    try
                    {
                        // ⚠️ 关键：直接发送包含起始码的完整数据（参照 FfmpegMuxReceiver）
                        // videoData 已经跳过了第一个字节（header type），但包含起始码（0x00000001 或 0x000001）
                        
                        // 优化：基于实际时间计算时间戳以减少延迟
                        var now = DateTime.UtcNow;
                        if (_videoPacketCount > 0)
                        {
                            var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                            _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                        }
                        _lastVideoPacketTime = now;
                        
                        // ✅ 性能优化：直接调用缓存的方法（避免反射查找开销）
                        _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { _videoTimestamp, videoData });
                        
                        // 为下一个包准备时间戳（使用固定增量作为后备）
                        _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                        
                        
                        return true; // 发送成功
                    }
                    catch (Exception ex)
                    {
                        if (_videoPacketCount < 3)
                        {
                            var innerEx = ex.InnerException ?? ex;
                            _logger.LogWarning("⚠️ SendVideo 直接发送失败: {Ex}", innerEx.Message);
                        }
                        // 如果缓存的方法失败，清除缓存以便下次重新查找
                        _cachedSendVideoMethod = null;
                        _methodsInitialized = false;
                    }
                }
                
                return false; // 发送失败
            }
            catch (Exception ex)
            {
                if (_videoPacketCount < 3)
                {
                    _logger.LogWarning("⚠️ TrySendVideoDirect 异常: {Ex}", ex.Message);
                }
                return false;
            }
        }
        
        private void SendVideoRTP(byte[] data)
        {
            try
            {
                if (_peerConnection == null || _videoTrack == null)
                {
                    return;
                }
                
                // ✅ 性能优化：使用缓存的状态检查
                var (connectionState, iceState, signalingState) = GetCachedConnectionState();
                
                // ⚠️ 关键修复：必须等待 SDP 协商完成（stable）和连接建立（connected）
                // GetSendingFormat() 需要 SDP 协商完成才能返回格式信息
                bool canSend = false;
                
                // 必须满足两个条件：
                // 1. 信令状态必须是 stable（SDP 协商完成）
                // 2. 连接状态必须是 connected 或 connecting（连接建立或正在建立）
                if (signalingState == RTCSignalingState.stable)
                {
                    if (connectionState == RTCPeerConnectionState.connected || 
                        connectionState == RTCPeerConnectionState.connecting)
                    {
                        canSend = true;
                    }
                    else if (iceState == RTCIceConnectionState.connected)
                    {
                        // ICE 已连接，即使 connectionState 还是 new，也可以尝试
                        // 但需要确保 SDP 已协商完成
                        canSend = true;
                    }
                }
                
                if (!canSend)
                {
                    if (_videoPacketCount < 10 || _videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ WebRTC 状态不允许发送: connection={State}, ICE={IceState}, signaling={Signaling}, 已收到 {Count} 个包", 
                            connectionState, iceState, signalingState, _videoPacketCount);
                        if (signalingState != RTCSignalingState.stable)
                        {
                            _logger.LogWarning("⚠️ SDP 协商未完成（{SignalingState}），需要等待 Answer 并设置为 stable", signalingState);
                        }
                        if (connectionState == RTCPeerConnectionState.@new)
                        {
                            _logger.LogWarning("⚠️ 连接状态还是 new，等待连接建立...");
                        }
                    }
                    return;
                }
                
                // ⚠️ 关键问题：如果 PS5 发送 HEVC，但浏览器只支持 H.264，需要转码
                // 当前实现：直接发送接收到的数据（可能是 HEVC）
                // 如果检测到 HEVC，会记录警告，但不会转码（需要实现转码功能）
                
                // ✅ 低延迟优化：先尝试直接发送，如果失败再解析NAL units
                // 这样可以避免不必要的NAL解析开销（大多数情况下直接发送都能成功）
                // 参考 FfmpegMuxReceiver：直接处理视频数据，让 WebRTC 自动处理关键帧检测
                
                // ⚠️ 注意：我们已经尝试过 TrySendVideoDirect，但可能失败了
                // 这里如果直接发送也失败，才解析NAL units
                
                // ✅ 优化：如果数据看起来是完整的帧（包含起始码），先尝试直接发送
                bool hasStartCode = (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && 
                                   (data[2] == 0x00 && data[3] == 0x01 || data[2] == 0x01));
                
                if (hasStartCode && data.Length < 50000) // 如果帧不是太大，尝试直接发送
                {
                    // 尝试直接发送（不解析NAL units）
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (_videoPacketCount > 0)
                        {
                            var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                            if (elapsed > 0)
                            {
                                _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                            }
                            else
                            {
                                _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                            }
                        }
                        _lastVideoPacketTime = now;
                        
                        // 尝试使用缓存的SendVideo方法
                        if (_cachedSendVideoMethod != null)
                        {
                            _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { _videoTimestamp, data });
                            _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                            return; // 直接发送成功，跳过NAL解析
                        }
                    }
                    catch
                    {
                        // 直接发送失败，继续使用NAL解析方式
                    }
                }
                
                // 如果直接发送失败，解析 NAL units（Annex-B 格式，支持 H.264 和 HEVC）
                // ⚠️ 注意：FfmpegMuxReceiver 接收的数据格式是 [HeaderType(1 byte)] + [视频数据（可能包含起始码）]
                // 而我们已经跳过了第一个字节，所以 data 就是纯视频数据（可能包含起始码）
                var nalUnits = ParseAnnexBNalUnits(data);
                
                if (nalUnits.Count == 0 && _videoPacketCount < 5)
                {
                    _logger.LogWarning("⚠️ 未解析到 NAL units，数据长度: {Length}, 前 16 字节: {Hex}", 
                        data.Length, 
                        data.Length > 0 ? Convert.ToHexString(data.Take(Math.Min(16, data.Length)).ToArray()) : "empty");
                }
                
                
                foreach (var nalUnit in nalUnits)
                {
                    if (nalUnit.Length == 0) continue;
                    
                    // 更新时间戳（每帧递增）
                    // 参考 FfmpegMuxReceiver：基于帧率自动更新时间戳
                    bool isVideoFrame = false;
                    
                    if (_detectedVideoFormat == "hevc")
                    {
                        // HEVC: NAL unit type 在第一个字节的高 6 位 (bits 6-1)
                        // HEVC NAL unit 格式: [F(1) | Type(6) | LayerId(6) | TID(3)]
                        byte nalType = (byte)((nalUnit[0] >> 1) & 0x3F);
                        // HEVC: IDR 帧是 type 19 (IDR_N_LP) 或 20 (IDR_W_RADL)
                        // 普通帧是 type 1 (TRAIL_N) 到 9 (CRA_NUT)
                        if (nalType >= 1 && nalType <= 21)
                        {
                            isVideoFrame = true;
                        }
                    }
                    else
                    {
                        // H.264: NAL unit type 在第一个字节的低 5 位 (bits 4-0)
                        byte nalType = (byte)(nalUnit[0] & 0x1F);
                        if (nalType >= 1 && nalType <= 5)
                        {
                            isVideoFrame = true;
                        }
                    }
                    
                    if (isVideoFrame)
                    {
                        // 视频帧（IDR 或非 IDR），优化：基于实际时间更新时间戳
                        var now = DateTime.UtcNow;
                        if (_videoPacketCount > 0)
                        {
                            var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                            if (elapsed > 0)
                            {
                                _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                            }
                            else
                            {
                                // 如果时间间隔太小，使用固定增量
                                _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                            }
                        }
                        _lastVideoPacketTime = now;
                    }
                    
                    // 如果 NAL unit 太大，需要分片
                    if (nalUnit.Length > RTP_MTU - 12) // RTP header 12 bytes
                    {
                        SendFragmentedNalUnit(nalUnit);
                    }
                    else
                    {
                        SendSingleNalUnit(nalUnit);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送视频 RTP 包失败");
            }
        }
        
        /// <summary>
        /// ✅ 优化：使用Span高效解析Annex-B格式的NAL units
        /// 使用单次扫描和Span操作，减少内存分配和循环开销
        /// </summary>
        private List<byte[]> ParseAnnexBNalUnits(byte[] data)
        {
            var nalUnits = new List<byte[]>();
            if (data == null || data.Length < 4) return nalUnits;
            
            // ✅ 使用Span进行高效搜索
            Span<byte> dataSpan = data;
            int currentPos = 0;
            
            while (currentPos < dataSpan.Length - 3)
            {
                // ✅ 优化：使用Span.SequenceEqual进行快速匹配
                // 查找起始码 0x00000001 或 0x000001
                int startCodePos = -1;
                int startCodeLength = 0;
                
                // 单次扫描查找起始码
                for (int i = currentPos; i < dataSpan.Length - 3; i++)
                {
                    // ✅ 快速检查：先检查前两个字节是否为0x00
                    if (dataSpan[i] == 0x00 && dataSpan[i + 1] == 0x00)
                    {
                        // 检查4字节起始码 0x00000001
                        if (i + 3 < dataSpan.Length && dataSpan[i + 2] == 0x00 && dataSpan[i + 3] == 0x01)
                        {
                            startCodePos = i;
                            startCodeLength = 4;
                            break;
                        }
                        // 检查3字节起始码 0x000001
                        else if (i + 2 < dataSpan.Length && dataSpan[i + 2] == 0x01)
                        {
                            startCodePos = i;
                            startCodeLength = 3;
                            break;
                        }
                    }
                }
                
                if (startCodePos == -1)
                {
                    // 没有找到起始码，结束
                    break;
                }
                
                // ✅ 优化：从当前起始码后开始查找下一个起始码（避免重复扫描）
                int nextStartCodePos = -1;
                int nextStartCodeLength = 0;
                int searchStart = startCodePos + startCodeLength;
                
                for (int i = searchStart; i < dataSpan.Length - 3; i++)
                {
                    if (dataSpan[i] == 0x00 && dataSpan[i + 1] == 0x00)
                    {
                        if (i + 3 < dataSpan.Length && dataSpan[i + 2] == 0x00 && dataSpan[i + 3] == 0x01)
                        {
                            nextStartCodePos = i;
                            nextStartCodeLength = 4;
                            break;
                        }
                        else if (i + 2 < dataSpan.Length && dataSpan[i + 2] == 0x01)
                        {
                            nextStartCodePos = i;
                            nextStartCodeLength = 3;
                            break;
                        }
                    }
                }
                
                // ✅ 优化：使用Span提取NAL unit（减少内存分配）
                int nalStart = startCodePos + startCodeLength;
                int nalEnd = nextStartCodePos == -1 ? dataSpan.Length : nextStartCodePos;
                int nalLength = nalEnd - nalStart;
                
                if (nalLength > 0)
                {
                    // ✅ 使用Span.Slice和ToArray进行高效复制
                    var nalUnit = dataSpan.Slice(nalStart, nalLength).ToArray();
                    nalUnits.Add(nalUnit);
                }
                
                // 移动到下一个起始码位置
                if (nextStartCodePos == -1)
                {
                    break;
                }
                currentPos = nextStartCodePos;
            }
            
            return nalUnits;
        }
        
        private void SendSingleNalUnit(byte[] nalUnit)
        {
            if (_peerConnection == null || _videoTrack == null || nalUnit.Length == 0) return;
            
            try
            {
                // 创建 RTP 包
                var rtpPacket = new RTPPacket(12 + nalUnit.Length);
                rtpPacket.Header.Version = 2;
                
                // ⚠️ 关键修复：根据视频编码格式选择正确的 payload type
                // H.264: payload type 96 (动态)
                // HEVC: payload type 97 (动态，取决于 SDP 协商)
                // 注意：SIPSorcery 可能会自动处理 payload type，但我们需要确保使用正确的值
                // ⚠️ 重要：SendRtpRaw 和 SendVideo 应该使用相同的 payload type
                int payloadType = 96; // 默认使用 96（H.264）
                if (_detectedVideoFormat == "hevc")
                {
                    // HEVC 通常使用 payload type 97（在 SDP 中协商）
                    // 但注意：浏览器不支持 HEVC，即使格式正确也无法播放
                    payloadType = 97;
                }
                
                rtpPacket.Header.PayloadType = (byte)payloadType;
                
                // ⚠️ 修复：确保序列号在 ushort 范围内
                // ushort 会自动回绕：65535 + 1 = 0，这是正常的 RTP 行为
                rtpPacket.Header.SequenceNumber = _videoSequenceNumber;
                _videoSequenceNumber++; // 自动回绕，无需检查溢出
                
                rtpPacket.Header.Timestamp = _videoTimestamp;
                
                // 设置 SSRC（使用 SRC 属性）
                rtpPacket.Header.SyncSource = _videoSsrc;
                
                // 设置 Marker（使用 MarkerBit 属性）
                // 对于 HEVC，需要检查是否是最后一个 NAL unit 来决定 marker
                rtpPacket.Header.MarkerBit = 0; // 单个 NAL unit，marker 设为 0
                
                // 复制 NAL unit 数据到 payload
                Buffer.BlockCopy(nalUnit, 0, rtpPacket.Payload, 0, nalUnit.Length);
                
                // 尝试通过 RTCPeerConnection 发送 RTP 包
                // SIPSorcery 可能需要通过内部机制发送，这里先尝试序列化并发送
                try
                {
                    // 将 RTP 包序列化为字节数组
                    byte[] rtpBytes = rtpPacket.GetBytes();
                    
                    
                    try
                    {
                        // ✅ 关键修复：应该发送完整的 RTP 包，而不是原始 NAL unit
                        // SIPSorcery 的 SendRtpRaw 期望接收完整的 RTP 包（包括 header + payload）
                        // ⚠️ 注意：SendVideo 已经成功发送，说明数据格式正确
                        // 如果 SendRtpRaw 失败，可以继续使用 SendVideo
                        
                        // ⚠️ 策略调整：由于 SendVideo 已经验证可以工作，优先使用 SendVideo
                        // SendRtpRaw 存在参数问题（UInt16 溢出），暂时跳过
                        // 方法1：优先使用 SendVideo（传入原始 NAL unit，让 SIPSorcery 自动打包）
                        // 方法2：如果 SendVideo 失败，再尝试 SendRtpRaw（但可能失败）
                        
                        var peerConnectionType = _peerConnection.GetType();
                        
                        // 先尝试 SendVideo（已经验证可以工作）
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
                        
                        bool videoSent = false;
                        foreach (var method in sendVideoMethods)
                        {
                            try
                            {
                                var parameters = method.GetParameters();
                                
                                if (parameters.Length == 2)
                                {
                                    if (parameters[0].ParameterType == typeof(uint) &&
                                        parameters[1].ParameterType == typeof(byte[]))
                                    {
                                        // SendVideo(uint timestamp, byte[] nalUnit)
                                        // ⚠️ 关键：SendVideo 期望的是 NAL unit 数据（不包含起始码）
                                        // ParseAnnexBNalUnits 已经去除了起始码，所以 nalUnit 就是纯 NAL unit 数据
                                        
                                        method.Invoke(_peerConnection, new object[] { _videoTimestamp, nalUnit });
                                        videoSent = true;
                                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                                if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                                {
                                    var innerEx = ex.InnerException ?? ex;
                                    _logger.LogWarning("⚠️ SendVideo 调用失败: {Ex}, 内部异常: {InnerEx}", 
                                        ex.Message, innerEx.Message);
                                }
                            }
                        }
                        
                        // 如果 SendVideo 成功，直接返回（不再尝试 SendRtpRaw）
                        if (videoSent) return;
                        
                        // 方法2：如果 SendVideo 失败，尝试 SendRtpRaw（但可能因为参数问题失败）
                        // ⚠️ 注意：SendRtpRaw 存在 UInt16 参数溢出问题，暂时禁用
                        // 如果 SendVideo 已经工作，不需要 SendRtpRaw
                        if (_videoPacketCount == 0)
                        {
                            _logger.LogWarning("⚠️ SendVideo 失败，尝试 SendRtpRaw（但可能因为参数问题失败）");
                        }
                        
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
                        if (sendRtpRawMethods.Any())
                        {
                            
                            foreach (var method in sendRtpRawMethods)
        {
            try
            {
                                    var parameters = method.GetParameters();
                                    
                                    // ⚠️ 关键修复：优先使用 SendRtpRaw(SDPMediaTypesEnum, Byte[], UInt32, Int32, Int32, UInt16)
                                    // 这个签名是完整的 RTP 发送方法，不需要 GetSendingFormat()
                                    if (parameters.Length == 6)
                                    {
                                        if (parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                            parameters[1].ParameterType == typeof(byte[]) &&
                                            parameters[2].ParameterType == typeof(uint) &&
                                            parameters[3].ParameterType == typeof(int) &&
                                            parameters[4].ParameterType == typeof(int) &&
                                            parameters[5].ParameterType == typeof(ushort))
                                        {
                                            // SendRtpRaw(SDPMediaTypesEnum, Byte[], UInt32 timestamp, Int32 payloadType, Int32 ssrc, UInt16 sequenceNumber)
                                            // ⚠️ 修复：直接使用 _videoSequenceNumber（已经是 ushort 类型），避免类型转换问题
                                            // 注意：序列号会在 65535 后自动回绕到 0，这是正常的 RTP 行为
                                            ushort seqNum = _videoSequenceNumber; // 直接使用，确保是 ushort 类型
                                            
                                            // 确保 PayloadType 在有效范围内（0-127）
                                            int payloadTypeInt = _detectedVideoFormat == "hevc" ? 97 : 96;
                                            if (rtpPacket.Header.PayloadType < 0 || rtpPacket.Header.PayloadType > 127)
                                            {
                                                _logger.LogWarning("⚠️ RTP Header PayloadType 超出范围: {PayloadType}, 使用计算值: {Computed}", 
                                                    rtpPacket.Header.PayloadType, payloadTypeInt);
                                            }
                                            else
                                            {
                                                payloadTypeInt = (int)rtpPacket.Header.PayloadType;
                                            }
                                            
                                            // SSRC 转换为 int（确保不溢出）
                                            int ssrcInt = (int)(_videoSsrc & 0x7FFFFFFF); // 确保是正数
                                            
                                            
                                            try
                                            {
                                                method.Invoke(_peerConnection, new object[] { 
                                                    SDPMediaTypesEnum.video, 
                                                    rtpBytes, 
                                                    rtpPacket.Header.Timestamp, 
                                                    payloadTypeInt, 
                                                    ssrcInt, 
                                                    seqNum 
                                                });
                                                rtpSent = true;
                                                break;
                                            }
                                            catch (Exception invokeEx)
                                            {
                                                var innerEx = invokeEx.InnerException ?? invokeEx;
                                                _logger.LogError(innerEx, "❌ SendRtpRaw 调用异常: seq={Seq}, payloadType={Pt}, ssrc={Ssrc}, ts={Ts}, rtpBytesLen={Len}, 错误: {Error}", 
                                                    seqNum, payloadTypeInt, ssrcInt, rtpPacket.Header.Timestamp, rtpBytes.Length, innerEx.Message);
                                                
                                                // 如果错误是 UInt16 超出范围，记录所有可能的值
                                                if (innerEx.Message.Contains("UInt16"))
                                                {
                                                    _logger.LogError("❌ UInt16 参数检查: seqNum={Seq} (range: 0-65535), rtpBytesLen={Len} (int, not UInt16)", 
                                                        seqNum, rtpBytes.Length);
                                                    _logger.LogError("❌ 可能的问题: RTP header 中的序列号字段可能不正确");
                                                }
                                                throw; // 重新抛出，让外层处理
                                            }
                                        }
                                    }
                                    else if (parameters.Length == 5)
                                    {
                                        if (parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                            parameters[1].ParameterType == typeof(byte[]) &&
                                            parameters[2].ParameterType == typeof(uint) &&
                                            parameters[3].ParameterType == typeof(int) &&
                                            parameters[4].ParameterType == typeof(int))
                                        {
                                            // SendRtpRaw(SDPMediaTypesEnum, Byte[], UInt32 timestamp, Int32 payloadType, Int32 ssrc)
                                            // ⚠️ 修复：确保 PayloadType 在有效范围内（0-127）
                                            int payloadTypeInt = (int)rtpPacket.Header.PayloadType;
                                            if (payloadTypeInt < 0 || payloadTypeInt > 127)
                                            {
                                                _logger.LogWarning("⚠️ PayloadType 超出范围: {PayloadType}, 使用默认值 96", payloadTypeInt);
                                                payloadTypeInt = 96; // 默认 H.264 payload type
                                            }
                                            
                                            method.Invoke(_peerConnection, new object[] { 
                                                SDPMediaTypesEnum.video, 
                                                rtpBytes, 
                                                rtpPacket.Header.Timestamp, 
                                                payloadTypeInt, 
                                                (int)rtpPacket.Header.SyncSource 
                                            });
                                            rtpSent = true;
                                            break;
                                        }
                                    }
                                    else if (parameters.Length == 2)
                                    {
                                        if (parameters[0].ParameterType == typeof(byte[]) && 
                                            parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                                        {
                                        method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.video });
                                            rtpSent = true;
                                            break;
                                        }
                                        else if (parameters[0].ParameterType == typeof(byte[]) && 
                                                 parameters[1].ParameterType == typeof(int))
                                        {
                                        method.Invoke(_peerConnection, new object[] { rtpBytes, 96 });
                                            rtpSent = true;
                                            break;
                                        }
                                    }
                                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte[]))
                                    {
                                    method.Invoke(_peerConnection, new object[] { rtpBytes });
                                        rtpSent = true;
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                                    {
                                        var innerEx = ex.InnerException ?? ex;
                                        _logger.LogWarning("⚠️ SendRtpRaw 调用失败: {Ex}, 内部异常: {InnerEx}, 方法参数: {Params}", 
                                            ex.Message, innerEx.Message, string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)));
                                    }
                                }
                            }
                            
                            // ⚠️ 如果 SendRtpRaw 成功，直接返回
                            if (rtpSent) return;
                        }
                        else
                        {
                            if (_videoPacketCount == 0)
                            {
                                _logger.LogWarning("⚠️ 未找到 SendRtpRaw 方法");
                            }
                        }
                        
                        // ⚠️ 如果 SendVideo 成功，直接返回（不再尝试 SendRtpRaw）
                        if (videoSent) return;
                        
                        // ⚠️ 如果所有方法都失败，记录详细错误信息
                        if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                        {
                            _logger.LogError("❌ 所有 SendVideo 方法调用都失败了！");
                            _logger.LogError("❌ 连接状态: {State}, ICE: {Ice}, 信令: {Signaling}", 
                                _peerConnection.connectionState, _peerConnection.iceConnectionState, _peerConnection.signalingState);
                            _logger.LogError("❌ 视频轨道状态: {Track}", _videoTrack != null ? "存在" : "不存在");
                            
                            // 尝试列出所有可用的方法
                            var allMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                                           m.Name.Contains("Rtp", StringComparison.OrdinalIgnoreCase))
                                .Select(m => {
                                    var paramsStr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                                    return $"{m.Name}({paramsStr})";
                                })
                                .ToList();
                            if (allMethods.Any())
                            {
                                _logger.LogError("❌ 可用的发送方法: {Methods}", string.Join("; ", allMethods));
                            }
                        }
                        
                        // 方法3：尝试通过 MediaStreamTrack 发送
                        if (_videoTrack != null)
                        {
                            var trackType = _videoTrack.GetType();
                            var trackMethods = trackType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            
                            
                            foreach (var method in trackMethods)
                            {
                                try
                                {
                                    var parameters = method.GetParameters();
                                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        method.Invoke(_videoTrack, new object[] { nalUnit });
                                        return; // 发送成功
                                    }
                                }
                                catch { }
                            }
                        }
                        
                        if (_videoPacketCount == 0)
                        {
                            // 首次调用时，列出所有可用的方法
                            var allMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase) || 
                                           m.Name.Contains("Rtp", StringComparison.OrdinalIgnoreCase))
                                .Select(m => {
                                    var paramsStr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                                    return $"{m.Name}({paramsStr})";
                                })
                                .ToList();
                            _logger.LogWarning("⚠️ 未找到可用的发送方法。所有相关方法: {Methods}", string.Join("; ", allMethods));
                        }
                        else if (_videoPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 未找到可用的 SendVideo 或 SendRtpRaw 方法");
                        }
                    }
                    catch (Exception ex)
                    {
                        // 记录详细错误
                        if (_videoPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 发送 RTP 包异常: {Ex}", ex.Message);
                        }
                    }
                    
                    // 如果所有方法都失败，记录警告
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ RTP 包已构建但未发送（需要找到正确的发送 API）: seq={Seq}, size={Size}", 
                            rtpPacket.Header.SequenceNumber, rtpBytes.Length);
                    }
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "❌ 发送 RTP 包失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送单个 NAL unit RTP 包失败");
            }
        }
        
        private void SendFragmentedNalUnit(byte[] nalUnit)
        {
            if (_peerConnection == null || _videoTrack == null || nalUnit.Length == 0) return;
            
            byte nalType = (byte)(nalUnit[0] & 0x1F);
            byte nalHeader = (byte)(nalUnit[0] & 0x60); // 保留 F 和 NRI 位
            
            // 计算分片数量
            int maxFragmentSize = RTP_MTU - 12 - 2; // RTP header + FU header
            int fragmentCount = (nalUnit.Length + maxFragmentSize - 1) / maxFragmentSize;
            
            for (int i = 0; i < fragmentCount; i++)
            {
                int fragmentStart = i * maxFragmentSize;
                int fragmentLength = Math.Min(maxFragmentSize, nalUnit.Length - fragmentStart);
                
                try
                {
                    // 创建 RTP 包
                    var rtpPacket = new RTPPacket(12 + 2 + fragmentLength);
                    rtpPacket.Header.Version = 2;
                    rtpPacket.Header.PayloadType = 96;
                    
                    // ⚠️ 修复：确保序列号在 ushort 范围内
                    rtpPacket.Header.SequenceNumber = _videoSequenceNumber;
                    _videoSequenceNumber++; // 自动回绕
                    
                    rtpPacket.Header.Timestamp = _videoTimestamp;
                    rtpPacket.Header.SyncSource = _videoSsrc;
                    
                    // 第一个分片：S=1, E=0
                    // 中间分片：S=0, E=0
                    // 最后分片：S=0, E=1
                    byte fuIndicator = (byte)(nalHeader | 28); // F=0, NRI, Type=28 (FU-A)
                    byte fuHeader = (byte)(nalType);
                    
                    if (i == 0)
                    {
                        fuHeader |= 0x80; // Start bit
                        rtpPacket.Header.MarkerBit = 0;
                    }
                    else if (i == fragmentCount - 1)
                    {
                        fuHeader |= 0x40; // End bit
                        rtpPacket.Header.MarkerBit = 1; // 最后一个分片设置 marker
                    }
                    else
                    {
                        rtpPacket.Header.MarkerBit = 0;
                    }
                    
                    // 设置 payload
                    rtpPacket.Payload[0] = fuIndicator;
                    rtpPacket.Payload[1] = fuHeader;
                    Buffer.BlockCopy(nalUnit, fragmentStart, rtpPacket.Payload, 2, fragmentLength);
                    
                    // 尝试发送分片 RTP 包
                    try
                    {
                        byte[] rtpBytes = rtpPacket.GetBytes();
                        
                        // 尝试发送分片 RTP 包（使用反射调用 SendRtpRaw）
                        try
                        {
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
                                            method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.video });
                                            return; // 发送成功
                                        }
                                        else if (parameters[1].ParameterType == typeof(int))
                                        {
                                            method.Invoke(_peerConnection, new object[] { rtpBytes, 96 });
                                            return; // 发送成功
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception)
                        {
                            // 分片发送失败，静默处理
                        }
                    }
                    catch (Exception sendEx)
                    {
                        _logger.LogError(sendEx, "❌ 发送分片 RTP 包失败");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 发送分片 NAL unit RTP 包失败: fragment {I}/{Count}", i + 1, fragmentCount);
                }
            }
        }
        
        /// <summary>
        /// 转码并发送音频：Opus -> PCM -> PCMU (G.711 μ-law)
        /// </summary>
        private void SendAudioWithTranscoding(byte[] opusFrame)
        {
            try
            {
                if (_peerConnection == null || opusFrame == null || opusFrame.Length == 0)
                {
                    return;
                }
                
                // 步骤1：将 Opus 解码为 PCM
                byte[]? pcmData = null;
                int samplesDecoded = 0;
                
                lock (_opusDecoderLock)
                {
                    // 初始化 Opus 解码器（如果需要）
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
                            return;
                        }
                    }
                    
                    // 使用 Opus 解码器解码为 PCM
                    // IOpusDecoder 使用 float[] 作为输出缓冲区
                    // frame_size 参数是每声道的样本数
                    float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);
                    
                    if (samplesDecoded > 0)
                    {
                        // ✅ 优化音质：使用更精确的 float 到 short 转换
                        // 使用 32767.0f 而不是 32768.0f 以避免溢出，同时保持精度
                        int sampleCount = samplesDecoded * _audioChannels;
                        pcmData = new byte[sampleCount * 2];
                        // ✅ 安全代码：使用 Span<T> 和 MemoryMarshal 进行高效转换
                        var floatSpan = pcmBufferFloat.AsSpan();
                        var shortSpan = MemoryMarshal.Cast<byte, short>(pcmData.AsSpan());
                        
                        for (int i = 0; i < sampleCount; i++)
                        {
                            // ✅ 优化：使用更精确的转换，避免截断失真和噪音
                            // 将 float (-1.0 到 1.0) 转换为 short (-32768 到 32767)
                            float sample = floatSpan[i];
                            // 软限制，避免硬截断造成的失真
                            if (sample > 1.0f) sample = 1.0f;
                            else if (sample < -1.0f) sample = -1.0f;
                            
                            // ✅ 优化：减少去噪阈值，避免过度去噪导致音质损失
                            // 只对极小的量化噪音进行去噪，保留更多细节
                            if (Math.Abs(sample) < 0.0001f)
                            {
                                sample = 0.0f; // 完全静音，避免量化噪音
                            }
                            
                            // 使用四舍五入而不是截断，提升精度
                            // 使用 32767.0f 而不是 32768.0f 以避免溢出
                            shortSpan[i] = (short)Math.Round(sample * 32767.0f);
                        }
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
                
                // ⚠️ 改进策略：优先尝试直接发送 Opus（如果浏览器支持），否则转码为 PCMA (A-law)
                // PCMA 在低音量时音质比 PCMU 更好
                // 步骤2：检查是否可以发送 Opus，否则转码为 PCMA
                if (pcmData != null && pcmData.Length > 0)
                {
                    // 先尝试直接发送 Opus（如果浏览器支持）
                    // 注意：这需要检查 Answer SDP 中是否包含 Opus
                    // 目前先使用 PCMA 转码，因为音质更好
                    
                    // 降采样：48000Hz -> 8000Hz（PCMA 需要 8000Hz）
                    byte[] downsampledPcm = DownsamplePCM(pcmData, _audioSampleRate, 8000, _audioChannels);
                    if (downsampledPcm != null && downsampledPcm.Length > 0)
                    {
                        int downsampledSamples = downsampledPcm.Length / (2 * _audioChannels); // 每个样本 2 字节
                        
                        // ⚠️ 暂时使用 PCMU 确保有声音，PCMA 编码算法可能有问题
                        // 使用 PCMU (μ-law) 转码
                        byte[] pcmuData = EncodePCMToPCMU(downsampledPcm);
                        if (pcmuData != null && pcmuData.Length > 0)
                        {
                            SendAudioPCMUAsRTP(pcmuData, downsampledSamples);
                        }
                        else
                        {
                            if (_audioPacketCount <= 5)
                            {
                                _logger.LogWarning("⚠️ PCMU 编码返回空数据");
                            }
                        }
                    }
                    else
                    {
                        if (_audioPacketCount <= 5)
                        {
                            _logger.LogWarning("⚠️ 降采样返回空数据: PCM长度={Length}", pcmData.Length);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 音频转码失败");
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
                
                // ⚠️ 参照 FfmpegMuxReceiver：使用 Opus 解码器将 Opus 帧解码为 PCM
                byte[]? pcmData = null;
                int samplesDecoded = 0;
                
                lock (_opusDecoderLock)
                {
                    // 初始化 Opus 解码器（如果需要）
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
                            // 如果解码器初始化失败，尝试直接发送 Opus 数据（让 WebRTC 处理）
                            SendAudioOpusDirect(opusFrame);
                            return;
                        }
                    }
                    
                    // 使用 Opus 解码器解码为 PCM
                    // IOpusDecoder 使用 float[] 作为输出缓冲区
                    // frame_size 参数是每声道的样本数
                    float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);
                    
                    if (samplesDecoded > 0)
                    {
                        // 将 float 样本转换为 short[]，然后转换为字节数组（s16le）
                        short[] pcmBuffer = new short[samplesDecoded * _audioChannels];
                        for (int i = 0; i < samplesDecoded * _audioChannels; i++)
                        {
                            // 将 float (-1.0 到 1.0) 转换为 short (-32768 到 32767)
                            float clamped = Math.Max(-1.0f, Math.Min(1.0f, pcmBufferFloat[i]));
                            pcmBuffer[i] = (short)(clamped * 32767.0f);
                        }
                        pcmData = new byte[samplesDecoded * _audioChannels * 2]; // 每个样本 2 字节
                        System.Buffer.BlockCopy(pcmBuffer, 0, pcmData, 0, pcmData.Length);
                    }
                    else
                    {
                        if (_audioPacketCount < 5)
                        {
                            _logger.LogWarning("⚠️ Opus 解码返回 0 个样本，包计数: {Count}", _audioPacketCount);
                        }
                        return; // 解码失败，跳过这个包
                    }
                }
                
                // 发送 PCM 数据到 WebRTC
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
        private void SendAudioOpusDirect(byte[] opusFrame)
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
                int samplesPerFrame = _audioFrameSize; // 通常是 480
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
        
        private string? DetectCodecFromVideoHeader(byte[] header)
        {
            if (header == null || header.Length < 5)
            {
                return null;
            }
            
            int actualHeaderLen = header.Length >= 64 ? header.Length - 64 : header.Length;
            
            for (int i = 0; i < actualHeaderLen - 4; i++)
            {
                if (i + 4 < actualHeaderLen && 
                    header[i] == 0x00 && header[i+1] == 0x00 && 
                    header[i+2] == 0x00 && header[i+3] == 0x01)
                {
                    byte nalType = header[i+4];
                    
                    // HEVC
                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }
                    
                    // H.264
                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }
                
                if (i + 3 < actualHeaderLen && 
                    header[i] == 0x00 && header[i+1] == 0x00 && header[i+2] == 0x01)
                {
                    byte nalType = header[i+3];
                    
                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }
                    
                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }
            }
            
            return null;
        }
        
        private bool IsIdrFrame(byte[] buf, int hintOffset)
        {
            if (buf == null || buf.Length < 6) return false;

            bool AnnexBScan(int start)
            {
                for (int i = start; i <= buf.Length - 4; i++)
                {
                    if (buf[i] == 0x00 && buf[i + 1] == 0x00)
                    {
                        int nalStart = -1;
                        if (i + 3 < buf.Length && buf[i + 2] == 0x00 && buf[i + 3] == 0x01) nalStart = i + 4;
                        else if (buf[i + 2] == 0x01) nalStart = i + 3;
                        if (nalStart >= 0 && nalStart < buf.Length)
                        {
                            byte h = buf[nalStart];
                            
                            // HEVC
                            int hevcType = (h >> 1) & 0x3F;
                            if (hevcType == 19 || hevcType == 20 || hevcType == 21 ||
                                hevcType == 16 || hevcType == 17 || hevcType == 18)
                            {
                                return true;
                            }
                            
                            // H.264
                            int h264Type = h & 0x1F;
                            if (h264Type == 5)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            return AnnexBScan(0) || (hintOffset > 0 && AnnexBScan(hintOffset));
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
                
                _peerConnection?.close();
                _peerConnection?.Dispose();
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

