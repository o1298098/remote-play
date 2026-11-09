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
using RemotePlay.Services.Statistics;
using RemotePlay.Services.Streaming.Receiver.Video;

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
        private ushort _audioSequenceNumber = 0;
        
        // ⚠️ 序列号会在 65535 后自动回绕到 0，这是 RTP 协议的正常行为
        private uint _audioTimestamp = 0;
        private readonly DateTime _epochStart = DateTime.UtcNow;
        
        // 统计信息
        private int _videoPacketCount;
        private int _audioPacketCount;
        private DateTime _startTime = DateTime.UtcNow;
        
        private DateTime _lastAudioPacketTime = DateTime.UtcNow;
        
        // 日志限流：避免重复警告日志洗版
        private DateTime _lastVideoPipelineWarningTime = DateTime.MinValue;
        private const int VIDEO_PIPELINE_WARNING_INTERVAL_SECONDS = 10; // 每 10 秒最多记录一次警告
        
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
        
        // ✅ 音频重置后同步机制
        private bool _audioResetting = false; // 是否正在重置音频
        private int _audioFramesToSkip = 0; // 重置后需要跳过的帧数
        private const int AUDIO_RESYNC_FRAMES = 1; // 重置后跳过1帧以重新同步（减少音频中断）
        
        private const uint AUDIO_CLOCK_RATE = 48000; // OPUS 音频时钟频率
        private const int RTP_SOCKET_BUFFER_SIZE = 4 << 20;
        
        // ✅ 协商后的动态负载类型（默认 H264=96, HEVC=97，协商成功后将覆盖）
        private int _negotiatedPtH264 = 96;
        private int _negotiatedPtHevc = 97;
        
        private VideoPipeline? _videoPipeline;
        private readonly object _videoPipelineLock = new();
        
        public event EventHandler? OnDisconnected;
        
        // ✅ 关键帧请求事件：当收到来自浏览器的 RTCP PLI/FIR 反馈时触发
        public event EventHandler? OnKeyframeRequested;
        
        // ✅ ICE Restart 请求事件：当 ICE 连接断开时触发
        public event EventHandler? OnIceRestartRequested;
        
        // 帧索引跟踪（用于延时统计）
        private long _currentVideoFrameIndex = 0;
        private long _currentAudioFrameIndex = 0;
        
        // ✅ 性能优化：缓存连接状态，减少属性访问开销
        private RTCPeerConnectionState? _cachedConnectionState;
        private RTCIceConnectionState? _cachedIceState;
        private RTCSignalingState? _cachedSignalingState;
        private DateTime _lastStateCheckTime = DateTime.MinValue;
        private const int STATE_CACHE_MS = 50; // 状态缓存50ms（视频60fps时每帧16ms）
        
        // ✅ 连接保活机制：使用 DataChannel keepalive（最有效），STUN Binding 作为备用
        private CancellationTokenSource? _keepaliveCts;
        private Task? _keepaliveTask;
        private DateTime _lastKeepaliveTime = DateTime.MinValue;
        private const int DATACHANNEL_KEEPALIVE_INTERVAL_MS = 5000; // DataChannel keepalive: 5秒（TURN连接需要更频繁的keepalive，避免NAT映射过期）
        private DateTime _lastVideoOrAudioPacketTime = DateTime.UtcNow;
        private RTCDataChannel? _keepaliveDataChannel; // DataChannel 用于 keepalive（最有效）
        private bool _dataChannelOpen = false; // ✅ DataChannel 是否已打开
        private readonly object _dataChannelLock = new object();
        
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
            
            _logger.LogInformation("🎬 WebRTCReceiver initialized - SessionId: {SessionId}", _sessionId);
            
            // 生成随机 SSRC
            var random = new Random();
            _videoSsrc = (uint)random.Next(1, int.MaxValue);
            _audioSsrc = (uint)random.Next(1, int.MaxValue);
            
            StartAudioSendLoop();
            ConfigureRtpSocketBuffers();
            
            // 监听连接状态变化（同时更新缓存）
            _peerConnection.onconnectionstatechange += (state) =>
            {
                _cachedConnectionState = state;
                _lastStateCheckTime = DateTime.UtcNow;
                _logger.LogInformation("📡 WebRTC connection state changed: {State} (video packets: {Count}, ICE state: {IceState})", 
                    state, _videoPacketCount, _peerConnection.iceConnectionState);
                if (state == RTCPeerConnectionState.connected)
                {
                    LogNegotiatedRtcpFeedback();

                    // ✅ 解析 SDP，获取浏览器协商的 H264/HEVC Payload Type
                    TryDetectNegotiatedVideoPayloadTypes();
                    
                    // ✅ 检测浏览器实际选择的音频编解码器
                    DetectSelectedAudioCodec();
                    
                    // ✅ 初始化新的模块化视频处理管道（在 SDP 协商完成后）
                    // 如果已经在 Answer 设置后提前初始化，这里不会重复初始化
                _logger.LogInformation("🔍 Connection established, initializing video pipeline: _videoTrack={Track}, _peerConnection={Pc}", 
                    _videoTrack != null, _peerConnection != null);
                    InitializeVideoPipeline();
                _logger.LogInformation("🔍 Video pipeline initialization done: _videoPipeline={Pipeline}", _videoPipeline != null);
                    
                    // ✅ 启动连接保活机制
                    StartKeepalive();
                }
                else if (state == RTCPeerConnectionState.failed || 
                    state == RTCPeerConnectionState.disconnected ||
                    state == RTCPeerConnectionState.closed)
                {
                    _logger.LogWarning("⚠️ WebRTC connection lost: {State}", state);
                    // ✅ 停止保活机制
                    StopKeepalive();
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    // ✅ 连接恢复时，如果之前是断开状态，可能需要请求关键帧
                    // 这可以处理网络中断后的恢复场景
                    if (_cachedConnectionState == RTCPeerConnectionState.disconnected ||
                        _cachedConnectionState == RTCPeerConnectionState.failed)
                    {
                        _logger.LogInformation("🔄 Connection restored, requesting keyframe to resync video");
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
                    _logger.LogWarning("⚠️ ICE connected but connectionState is still new; video sending may be affected");
                }
                
                // ✅ 如果 ICE 断开，延迟后尝试 ICE Restart（避免短暂抖动）
                if (state == RTCIceConnectionState.disconnected || 
                    state == RTCIceConnectionState.failed)
                {
                    _logger.LogWarning("⚠️ ICE disconnected: {State}, will attempt ICE restart after a delay", state);
                    StopKeepalive();
                    
                    // ✅ 延迟触发 ICE Restart（避免短暂抖动，disconnected 持续 > 10秒才触发）
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10000); // 等待 10 秒
                        
                        // ✅ 再次检查状态，确认仍然断开
                        if (_peerConnection != null && !_disposed)
                        {
                            var currentIceState = _peerConnection.iceConnectionState;
                            if (currentIceState == RTCIceConnectionState.disconnected || 
                                currentIceState == RTCIceConnectionState.failed)
                            {
                                _logger.LogInformation("🔄 ICE still disconnected, triggering ICE restart");
                                OnIceRestartRequested?.Invoke(this, EventArgs.Empty);
                            }
                        }
                    });
                }
                else if (state == RTCIceConnectionState.closed)
                {
                    _logger.LogWarning("⚠️ ICE connection closed: {State}", state);
                    StopKeepalive();
                }
                else if (state == RTCIceConnectionState.connected &&
                         _peerConnection.connectionState == RTCPeerConnectionState.connected)
                {
                    StartKeepalive();
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
            
            // ✅ 监听 RTCP 反馈（PLI/FIR 关键帧请求、接收报告）
            SubscribeRtcpFeedback();
            
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
                        _logger.LogInformation("✅ Negotiated H264 PayloadType: {Pt}", pt);
                    }
                    else if (codecPart.StartsWith("h265/") || codecPart.StartsWith("hevc/"))
                    {
                        _negotiatedPtHevc = pt;
                        _logger.LogInformation("✅ Negotiated HEVC PayloadType: {Pt}", pt);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "⚠️ Failed to parse video PayloadType, using defaults (H264=96, HEVC=97)");
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
                    _logger.LogWarning("⚠️ Cannot detect audio codec: remote description is null");
                    _useOpusDirect = false; // 回退到转码
                    return;
                }
                
                // ✅ SDP 对象需要转换为字符串
                string sdp = remoteDescription.sdp.ToString() ?? "";
                if (string.IsNullOrEmpty(sdp))
                {
                    _logger.LogWarning("⚠️ Cannot detect audio codec: SDP string is empty");
                    _useOpusDirect = false; // 回退到转码
                    return;
                }
                
                // 检查是否包含 Opus
                bool hasOpus = sdp.Contains("opus") || sdp.Contains("111");
                bool hasPCMU = sdp.Contains("PCMU") || sdp.Contains("a=rtpmap:0");
                
                _logger.LogInformation("🔊 Detected audio codecs: Opus={Opus}, PCMU={PCMU}", hasOpus, hasPCMU);
                
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
                            _logger.LogInformation("🔊 First format in m=audio line (browser choice): {Format}", selectedPayloadType);
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
                                    _logger.LogInformation("✅ Browser selected Opus (high quality): {Line}", trimmed);
                                }
                                else if (trimmed.Contains("PCMU") || payloadTypeStr == "0")
                                {
                                    _opusCodecDetected = false;
                                    _logger.LogWarning("⚠️ Browser selected PCMU: {Line}", trimmed);
                                }
                                else
                                {
                                    _opusCodecDetected = false;
                                    _logger.LogWarning("⚠️ Browser selected another codec: {Line}", trimmed);
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
                    _logger.LogInformation("🔄 Browser selected PCMU, but will try sending Opus for higher audio quality");
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
                    _logger.LogWarning("⚠️ Browser selected PCMU (8kHz); will try sending Opus for higher quality, falling back to transcoding on failure");
                }
                else
                {
                    _logger.LogInformation("✅ Browser selected Opus (48kHz, high quality); sending Opus directly without transcoding");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to detect audio codec, defaulting to transcoding");
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
            
            // ✅ 延迟初始化 VideoPipeline：将在连接建立后，SDP协商完成时初始化（确保 payload types 正确）
            // 在 onconnectionstatechange 中初始化（连接建立后）
        }
        
        /// <summary>
        /// 初始化新的模块化视频处理管道
        /// 应该在连接建立后、SDP协商完成后调用（确保 payload types 正确）
        /// </summary>
        /// <summary>
        /// ✅ 提前初始化视频管道（在 Answer 设置后，不等待连接建立）
        /// 这对于强制使用 TURN 的场景很重要，因为即使 ICE 连接失败，视频管道也应该初始化
        /// </summary>
        public void InitializeVideoPipelineEarly()
        {
            if (_peerConnection?.remoteDescription != null)
            {
                TryDetectNegotiatedVideoPayloadTypes();
                DetectSelectedAudioCodec();
            }

            InitializeVideoPipeline();
        }
        
        private void InitializeVideoPipeline()
        {
            lock (_videoPipelineLock)
            {
                if (_videoPipeline != null || _videoTrack == null || _peerConnection == null || _disposed)
                {
                    return;
                }

                try
                {
                    var pipeline = new VideoPipeline(_logger, _peerConnection, _detectedVideoFormat);
                    pipeline.SetOnPacketSent(frameIndex =>
                    {
                        _latencyStats?.RecordPacketSent(_sessionId, "video", frameIndex);
                    });
                    pipeline.SetOnKeyframeRequest(() => OnKeyframeRequested?.Invoke(this, EventArgs.Empty));
                    pipeline.SetDiagnosticsSuffix(GetRtcpDiagnostics);
                    _videoPipeline = pipeline;

                    _logger.LogInformation("✅ Video send pipeline initialized (Codec={Codec}, H264 PT={H264}, HEVC PT={Hevc})",
                        _detectedVideoFormat, _negotiatedPtH264, _negotiatedPtHevc);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to initialize video send pipeline");
                }
            }
        }

        /// <summary>
        /// 高码率下一个关键帧可达数百 KB，会瞬间突发写入 socket；默认发送缓冲区装不下时会丢包。
        /// </summary>
        private void ConfigureRtpSocketBuffers()
        {
            try
            {
                var socket = _peerConnection?.GetRtpChannel()?.RtpSocket;
                if (socket == null)
                {
                    return;
                }

                socket.SendBufferSize = RTP_SOCKET_BUFFER_SIZE;
                int effective = socket.SendBufferSize;
                if (effective < RTP_SOCKET_BUFFER_SIZE)
                {
                    _logger.LogWarning("⚠️ WebRTC RTP send buffer is only {Effective} bytes (requested {Requested}); packets may drop at high bitrates, increase host net.core.wmem_max",
                        effective, RTP_SOCKET_BUFFER_SIZE);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to set WebRTC RTP send buffer");
            }
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
            if (_preferredVideoCodec != null && (_preferredVideoCodec.ToLower() == "h264" || _preferredVideoCodec.ToLower() == "avc"))
            {
                _logger.LogInformation("🎯 WebRTC video track using preferred codec: H.264");
                return new List<SDPAudioVideoMediaFormat> { h264Format };
            }

            if (_preferredVideoCodec != null && (_preferredVideoCodec.ToLower() == "h265" || _preferredVideoCodec.ToLower() == "hevc"))
            {
                _logger.LogInformation("🎯 WebRTC video track using preferred codec: H.265/HEVC");
                return new List<SDPAudioVideoMediaFormat> { hevcFormat,h264Format };
            }

            // 未指定时，保持默认顺序：HEVC 优先，H.264 备用
            return new List<SDPAudioVideoMediaFormat> { hevcFormat, h264Format };
        }
        
        public void SetVideoCodec(string codec)
        {
            _detectedVideoFormat = codec.ToLower();
            _videoPipeline?.SetCodec(_detectedVideoFormat);
            _logger.LogInformation("📹 Video codec: {Codec}", codec);
        }
        
        public void SetAudioCodec(string codec)
        {
            // 音频编码格式已设置
        }
        
        public void EnterWaitForIdr()
        {
            // 通常发生在切换接收器或重新连接时
            _logger.LogInformation("🎬 Entering wait-for-IDR mode, requesting keyframe");
            var pipeline = _videoPipeline;
            if (pipeline != null)
            {
                pipeline.EnterWaitForIdr("receiver-wait-for-idr");
            }
            else
            {
                OnKeyframeRequested?.Invoke(this, EventArgs.Empty);
            }
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
                        _logger.LogInformation("📹 Video codec from stream info: {Old} -> {New}", _detectedVideoFormat, detectedCodec);
                        _detectedVideoFormat = detectedCodec;
                        _videoPipeline?.SetCodec(detectedCodec);
                    }
                }
                
                // ⚠️ 参照 FfmpegMuxReceiver：从 audioHeader 读取音频参数
                if (audioHeader == null || audioHeader.Length < 10)
                {
                    if (audioHeader == null)
                    {
                        _logger.LogWarning("⚠️ OnStreamInfo: audioHeader is null, skipping audio init");
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ OnStreamInfo: audioHeader too short ({Length} < 10), skipping audio init", audioHeader.Length);
                    }
                    return;
                }
                
                // audioHeader 有效，继续处理
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
                        _logger.LogInformation("🔊 Audio params: channels={Channels}, bits={Bits}, rate={Rate}Hz, frameSize={FrameSize}", channels, bitsPerSample, rate, frameSize);
                    }

                    if (channels != 2 && (_audioPacketCount < 5 || previousSourceChannels != channels))
                    {
                        _logger.LogWarning("⚠️ Host reports {Channels} audio channels; consider enabling stereo downmix or 2-channel output on the host", channels);
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
                        if (channels != _audioChannels)
                        {
                            _audioChannels = channels;
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
                                _logger.LogError(ex, "❌ Failed to initialize Opus decoder");
                                _opusDecoder = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to process StreamInfo");
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
            
            _logger.LogInformation("🛑 WebRTCReceiver disposing - video packets: {Video}, audio packets: {Audio}", 
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

                UnsubscribeRtcpFeedback();
                
                // ✅ 停止保活机制并清理 DataChannel
                StopKeepalive();
                
                lock (_dataChannelLock)
                {
                    try
                    {
                        _keepaliveDataChannel?.close();
                        _keepaliveDataChannel = null;
                    }
                    catch { }
                }
                
                VideoPipeline? pipeline;
                lock (_videoPipelineLock)
                {
                    pipeline = _videoPipeline;
                    _videoPipeline = null;
                }
                try
                {
                    pipeline?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Exception while disposing video send pipeline");
                }
                
                StopAudioSendLoop();
                
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
                            _logger.LogWarning("⚠️ WebRTC connection dispose timed out (1s), continuing anyway");
                        }
                        else
                        {
                            _logger.LogDebug("✅ WebRTC connection disposed");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Exception while disposing WebRTC connection");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to dispose WebRTC connection");
            }
        }
    }
}

