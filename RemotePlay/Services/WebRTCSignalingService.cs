using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Configuration;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace RemotePlay.Services
{
    /// <summary>
    /// WebRTC 信令服务 - 管理 WebRTC 连接和信令交换
    /// </summary>
    public class WebRTCSignalingService
    {
        private readonly ILogger<WebRTCSignalingService> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, WebRTCSession> _sessions;
        private readonly LatencyStatisticsService? _latencyStats;
        private readonly IControllerService? _controllerService;
        private readonly IStreamingService? _streamingService; // ✅ 用于请求关键帧
        private readonly WebRTCConfig _config;
        private readonly PortRange? _portRange;

        public WebRTCSignalingService(
            ILogger<WebRTCSignalingService> logger,
            ILoggerFactory loggerFactory,
            LatencyStatisticsService? latencyStats = null,
            IControllerService? controllerService = null,
            IStreamingService? streamingService = null,
            IOptions<WebRTCConfig>? webrtcOptions = null) // ✅ 注入 IStreamingService
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _latencyStats = latencyStats;
            _controllerService = controllerService;
            _streamingService = streamingService;
            _sessions = new ConcurrentDictionary<string, WebRTCSession>();

            _config = webrtcOptions?.Value ?? new WebRTCConfig();
            _portRange = CreatePortRange(_config);

            if (_portRange != null)
            {
                _logger.LogInformation("🌐 WebRTC 自定义端口范围: {Min}-{Max} (Shuffle={Shuffle})",
                    _config.IcePortMin,
                    _config.IcePortMax,
                    _config.ShufflePorts);
            }
            else if (_config.IcePortMin.HasValue || _config.IcePortMax.HasValue)
            {
                _logger.LogWarning("⚠️ WebRTC 端口范围配置无效，将回退为系统随机端口 (min={Min}, max={Max})",
                    _config.IcePortMin,
                    _config.IcePortMax);
            }
        }

        /// <summary>
        /// 创建新的 WebRTC 会话
        /// </summary>
        public async Task<(string sessionId, string offer)> CreateSessionAsync(
            string? preferredVideoCodec = null,
            bool? preferLanCandidatesOverride = null)
        {
            var sessionId = Guid.NewGuid().ToString("N");

            try
            {
                // ⚙️ 创建 WebRTC 配置（增加低延迟优化）
                var config = new RTCConfiguration
                {
                    // 若后续你添加 TURN，这里支持多项
                    iceServers = new List<RTCIceServer>
            {
                //new RTCIceServer { urls = "stun:stun.qcloudtrtc.com:8000" },
                //new RTCIceServer { urls = "stun:stun.alibabacloud.com:3478" },
               // new RTCIceServer { urls = "stun:stun.agora.io:3478" },
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            },
                    bundlePolicy = RTCBundlePolicy.max_bundle,
                    rtcpMuxPolicy = RTCRtcpMuxPolicy.require,
                    iceTransportPolicy = RTCIceTransportPolicy.all
                };

                if (_config.TurnServers?.Count > 0)
                {
                    int turnServerCount = 0;
                    foreach (var turn in _config.TurnServers.Where(t => !string.IsNullOrWhiteSpace(t.Url)))
                    {
                        config.iceServers.Add(new RTCIceServer
                        {
                            urls = turn.Url!,
                            username = turn.Username,
                            credential = turn.Credential
                        });
                        turnServerCount++;
                        _logger.LogInformation("🌐 添加 TURN 服务器: {Url} (用户名: {Username})", 
                            turn.Url, string.IsNullOrWhiteSpace(turn.Username) ? "无" : "已设置");
                    }
                    _logger.LogInformation("✅ 已配置 {Count} 个 TURN 服务器", turnServerCount);
                }
                else
                {
                    _logger.LogInformation("ℹ️ 未配置 TURN 服务器，将仅使用 STUN 和直接连接");
                }

                var peerConnection = new RTCPeerConnection(config, portRange: _portRange);

                // 🎯 创建接收器（视频/音频处理逻辑）
                var receiver = new WebRTCReceiver(
                    sessionId,
                    peerConnection,
                    _loggerFactory.CreateLogger<WebRTCReceiver>(),
                    _latencyStats,
                    preferredVideoCodec
                );

                var session = new WebRTCSession
                {
                    SessionId = sessionId,
                    PeerConnection = peerConnection,
                    Receiver = receiver,
                    CreatedAt = DateTime.UtcNow,
                    StreamingSessionId = null,
                    PreferredVideoCodec = preferredVideoCodec
                };

                _sessions.TryAdd(sessionId, session);

                // 🔌 监听断开事件
                receiver.OnDisconnected += async (s, e) =>
                {
                    _logger.LogInformation("🔌 WebRTC 会话断开: {SessionId}", sessionId);
                    await RemoveSessionAsync(sessionId);
                };

                // 🎬 监听浏览器的 PLI/FIR 请求（请求关键帧）
                receiver.OnKeyframeRequested += async (s, e) =>
                {
                    if (_streamingService != null && session.StreamingSessionId.HasValue)
                    {
                        try
                        {
                            var stream = await _streamingService.GetStreamAsync(session.StreamingSessionId.Value);
                            if (stream != null)
                            {
                                await stream.RequestKeyframeAsync();
                                _logger.LogInformation("✅ 请求关键帧成功: {SessionId}", session.StreamingSessionId.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 请求关键帧失败");
                        }
                    }
                };

                // 🧠 SDP Offer
                var offer = peerConnection.createOffer();
                await peerConnection.setLocalDescription(offer);

                _logger.LogInformation("✅ 创建 WebRTC 会话: {SessionId}, 状态: {State}, ICE: {IceState}",
                    sessionId, peerConnection.connectionState, peerConnection.iceConnectionState);

                // 🧊 等待 ICE Gathering
                var tcs = new TaskCompletionSource<bool>();
                int candidateCount = 0;
                int hostCandidateCount = 0;
                int srflxCandidateCount = 0;
                int relayCandidateCount = 0;
                bool gatheringComplete = false;
                bool hasTurnServers = _config.TurnServers?.Count > 0;

                peerConnection.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        candidateCount++;
                        
                        // 统计候选地址类型
                        var candidateStr = candidate.candidate?.ToLowerInvariant() ?? "";
                        if (candidateStr.Contains("typ host"))
                            hostCandidateCount++;
                        else if (candidateStr.Contains("typ srflx"))
                            srflxCandidateCount++;
                        else if (candidateStr.Contains("typ relay"))
                            relayCandidateCount++;
                        
                        try { peerConnection.addLocalIceCandidate(candidate); }
                        catch { /* 已自动添加 */ }
                        
                        // 记录TURN候选地址（重要）
                        if (candidateStr.Contains("typ relay"))
                        {
                            _logger.LogInformation("🌐 发现 TURN relay 候选地址: {Candidate}", candidate.candidate);
                        }
                    }
                    else
                    {
                        gatheringComplete = true;
                        _logger.LogInformation("🧊 ICE Gathering 完成，共 {Count} 个 candidates (host={Host}, srflx={Srflx}, relay={Relay})", 
                            candidateCount, hostCandidateCount, srflxCandidateCount, relayCandidateCount);
                        tcs.TrySetResult(true);
                    }
                };

                // ⚡ 根据是否配置TURN服务器调整等待时间
                // TURN服务器需要更长时间来建立连接和分配中继地址
                int waitTimeoutMs = hasTurnServers ? 8000 : 2000; // TURN: 8秒，无TURN: 2秒
                
                await Task.WhenAny(tcs.Task, Task.Delay(waitTimeoutMs));

                if (!gatheringComplete)
                {
                    _logger.LogWarning("⚠️ ICE Gathering 未完成（等待{Timeout}ms），已收集 {Count} 个 candidates (host={Host}, srflx={Srflx}, relay={Relay})。继续使用现有 SDP", 
                        waitTimeoutMs, candidateCount, hostCandidateCount, srflxCandidateCount, relayCandidateCount);
                    
                    // 如果配置了TURN但没有收集到relay候选，发出警告
                    if (hasTurnServers && relayCandidateCount == 0)
                    {
                        _logger.LogWarning("⚠️ 配置了TURN服务器但未收集到relay候选地址，请检查：1) TURN服务器是否可访问 2) 用户名密码是否正确 3) 防火墙是否开放UDP端口");
                    }
                }

                // 🧩 优化 SDP（低延迟关键）
                var finalSdp = OptimizeSdpForLowLatency(peerConnection.localDescription.sdp.ToString());
                finalSdp = ApplyPublicIpToSdp(finalSdp);
                finalSdp = PrioritizeLanCandidates(finalSdp, preferLanCandidatesOverride);

                // ✅ 验证SDP中是否包含TURN候选地址
                if (hasTurnServers)
                {
                    bool hasRelayCandidate = finalSdp.Contains("typ relay", StringComparison.OrdinalIgnoreCase);
                    if (hasRelayCandidate)
                    {
                        _logger.LogInformation("✅ SDP 中包含 TURN relay 候选地址");
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ SDP 中未找到 TURN relay 候选地址。可能原因：1) ICE gathering未完成 2) TURN服务器连接失败 3) 需要更长的等待时间");
                    }
                }

                return (sessionId, finalSdp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 创建 WebRTC 会话失败");
                _sessions.TryRemove(sessionId, out _);
                throw;
            }
        }

        private static PortRange? CreatePortRange(WebRTCConfig config)
        {
            if (!config.IcePortMin.HasValue || !config.IcePortMax.HasValue)
            {
                return null;
            }

            var min = config.IcePortMin.Value;
            var max = config.IcePortMax.Value;

            if (min <= 0 || max <= 0)
            {
                return null;
            }

            if (min > max)
            {
                return null;
            }

            if (min % 2 != 0)
            {
                min += 1;
            }

            if (max % 2 != 0)
            {
                max -= 1;
            }

            if (min > max)
            {
                return null;
            }

            return new PortRange(min, max, config.ShufflePorts);
        }
        /// <summary>
        /// 设置远端 Answer
        /// </summary>
        public async Task<bool> SetAnswerAsync(string sessionId, string answerSdp)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("⚠️ 会话不存在: {SessionId}", sessionId);
                return false;
            }

            try
            {
                if (!answerSdp.Contains("m=video"))
                {
                    _logger.LogWarning("⚠️ Answer SDP 中没有找到 m=video 行");
                }

                if (!answerSdp.Contains("m=audio"))
                {
                    _logger.LogWarning("⚠️ Answer SDP 中没有找到 m=audio 行");
                }

                var answer = new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = answerSdp
                };

                var result = session.PeerConnection.setRemoteDescription(answer);
                var signalingState = session.PeerConnection.signalingState;
                var connectionState = session.PeerConnection.connectionState;
                var iceState = session.PeerConnection.iceConnectionState;

                if (result == SetDescriptionResultEnum.OK)
                {
                    if (signalingState != RTCSignalingState.stable)
                    {
                        _logger.LogWarning("⚠️ Answer 设置返回 OK，但信令状态是 {Signaling}，不是 stable", signalingState);
                    }

                    return true;
                }
                else
                {
                    _logger.LogWarning("⚠️ 设置 Answer 返回非 OK 状态: {SessionId}, 结果: {Result}", sessionId, result);

                    if (result == SetDescriptionResultEnum.VideoIncompatible)
                    {
                        if (signalingState == RTCSignalingState.have_remote_pranswer ||
                            signalingState == RTCSignalingState.stable)
                        {
                            _logger.LogWarning("⚠️ 视频不兼容，但信令状态已改变为 {Signaling}，允许连接继续", signalingState);
                            return true;
                        }
                        else
                        {
                            _logger.LogError("❌ 视频不兼容且 Answer 未被设置，信令状态: {Signaling}", signalingState);
                            return true;
                        }
                    }
                    else if (result == SetDescriptionResultEnum.AudioIncompatible)
                    {
                        if (signalingState == RTCSignalingState.stable || signalingState == RTCSignalingState.have_remote_pranswer)
                        {
                            return true;
                        }
                        else
                        {
                            try
                            {
                                var remoteDesc = session.PeerConnection.remoteDescription;
                                if (remoteDesc != null && !string.IsNullOrWhiteSpace(remoteDesc.sdp?.ToString()))
                                {
                                    return true;
                                }
                                else
                                {
                                    var answerHasOpus = answerSdp.Contains("opus") || answerSdp.Contains("111");
                                    var answerHasTelephoneEvent = answerSdp.Contains("telephone-event") || answerSdp.Contains("101");

                                    if (!answerHasOpus && answerHasTelephoneEvent)
                                    {
                                        _logger.LogError("❌ 浏览器 Answer 中只包含 telephone-event，没有 Opus");
                                    }

                                    try
                                    {
                                        var peerConnectionType = session.PeerConnection.GetType();
                                        var setRemoteDescMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                            .Where(m => m.Name.Contains("Remote") && m.Name.Contains("Description"))
                                            .ToList();

                                        foreach (var method in setRemoteDescMethods)
                                        {
                                            try
                                            {
                                                var parameters = method.GetParameters();
                                                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(RTCSessionDescriptionInit))
                                                {
                                                    method.Invoke(session.PeerConnection, new object[] { answer });
                                                    var newRemoteDesc = session.PeerConnection.remoteDescription;
                                                    if (newRemoteDesc != null)
                                                    {
                                                        return true;
                                                    }
                                                }
                                            }
                                            catch { }
                                        }

                                        var remoteDescProperty = peerConnectionType.GetProperty("remoteDescription",
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (remoteDescProperty != null)
                                        {
                                            var setter = remoteDescProperty.GetSetMethod(true);
                                            if (setter != null)
                                            {
                                                setter.Invoke(session.PeerConnection, new object[] { answer });
                                                var newRemoteDesc = session.PeerConnection.remoteDescription;
                                                if (newRemoteDesc != null)
                                                {
                                                    return true;
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception forceEx)
                                    {
                                        _logger.LogError(forceEx, "❌ 强制设置 remote description 失败");
                                    }

                                    try
                                    {
                                        var newOffer = session.PeerConnection.createOffer();
                                        if (newOffer != null)
                                        {
                                            await session.PeerConnection.setLocalDescription(newOffer);
                                            await Task.Delay(100);
                                        }
                                    }
                                    catch { }

                                    return true;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ 检查 remote description 时出错");
                                return true;
                            }
                        }
                    }

                    // 其他错误返回 false
                    _logger.LogError("❌ 设置 Answer 失败: {SessionId}, 结果: {Result}", sessionId, result);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 设置 Answer 失败: {SessionId}", sessionId);
                return false;
            }
        }

        /// <summary>
        /// 添加 ICE Candidate
        /// </summary>
        public async Task<bool> AddIceCandidateAsync(string sessionId, string candidate, string sdpMid, ushort sdpMLineIndex)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("⚠️ 会话不存在: {SessionId}", sessionId);
                return false;
            }

            try
            {
                _logger.LogDebug("📥 接收 ICE Candidate: SessionId={SessionId}, Candidate={Candidate}, SdpMid={SdpMid}, SdpMLineIndex={SdpMLineIndex}",
                    sessionId, candidate, sdpMid, sdpMLineIndex);

                var iceCandidate = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid,
                    sdpMLineIndex = sdpMLineIndex
                };

                session.PeerConnection.addIceCandidate(iceCandidate);
                
                _logger.LogDebug("✅ ICE Candidate 已添加到 PeerConnection: SessionId={SessionId}, ConnectionState={ConnectionState}, IceConnectionState={IceConnectionState}",
                    sessionId, 
                    session.PeerConnection.connectionState,
                    session.PeerConnection.iceConnectionState);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 添加 ICE Candidate 失败: SessionId={SessionId}, Candidate={Candidate}", 
                    sessionId, candidate);
                return false;
            }
        }

        /// <summary>
        /// 获取接收器（用于连接到 AVHandler）
        /// </summary>
        public IAVReceiver? GetReceiver(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                return session.Receiver;
            }
            return null;
        }

        /// <summary>
        /// 获取会话信息
        /// </summary>
        public WebRTCSession? GetSession(string sessionId)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return session;
        }

        /// <summary>
        /// 获取所有会话
        /// </summary>
        public IEnumerable<WebRTCSession> GetAllSessions()
        {
            return _sessions.Values;
        }

        /// <summary>
        /// 移除会话
        /// </summary>
        public async Task RemoveSessionAsync(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                try
                {
                    // ✅ 自动停止流会话（如果存在）
                    if (_streamingService != null && session.StreamingSessionId.HasValue)
                    {
                        try
                        {
                            var stopped = await _streamingService.StopStreamAsync(session.StreamingSessionId.Value);
                            if (stopped)
                            {
                                _logger.LogInformation("✅ 流会话已停止: {StreamingSessionId}", session.StreamingSessionId.Value);
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ 停止流会话失败或流会话不存在: {StreamingSessionId}", session.StreamingSessionId.Value);
                            }
                        }
                        catch (Exception streamEx)
                        {
                            _logger.LogWarning(streamEx, "⚠️ 停止流会话时出错: {StreamingSessionId}", session.StreamingSessionId.Value);
                        }
                    }

                    // ✅ 自动断开控制器连接
                    if (_controllerService != null && Guid.TryParse(sessionId, out var sessionGuid))
                    {
                        try
                        {
                            await _controllerService.DisconnectAsync(sessionGuid);
                            _logger.LogInformation("🎮 控制器已自动断开: {SessionId}", sessionId);
                        }
                        catch (Exception controllerEx)
                        {
                            _logger.LogWarning(controllerEx, "⚠️ 断开控制器时出错: {SessionId}", sessionId);
                        }
                    }

                    session.Receiver?.Dispose();
                    session.PeerConnection?.close();
                    session.PeerConnection?.Dispose();
                    _logger.LogInformation("🗑️ WebRTC 会话已移除: {SessionId}", sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 移除会话时出错: {SessionId}", sessionId);
                }
            }
        }

        /// <summary>
        /// 移除会话（同步版本，向后兼容）
        /// </summary>
        public void RemoveSession(string sessionId)
        {
            // 异步调用但不等待（fire-and-forget）
            _ = RemoveSessionAsync(sessionId);
        }

        /// <summary>
        /// 优化 SDP 以降低延迟（更保守的方法，避免破坏 SDP 格式）
        /// </summary>
        private string OptimizeSdpForLowLatency(string sdp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sdp) || sdp.Length < 10)
                    return sdp;

                // 避免重复处理
                if (sdp.Contains("a=x-google-flag:low-latency") && sdp.Contains("a=minBufferedPlaybackTime"))
                    return sdp;

                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var optimizedLines = new List<string>();
                bool foundVideo = false, foundAudio = false;
                bool videoOptimized = false, audioOptimized = false;

                foreach (var line in lines)
                {
                    optimizedLines.Add(line);
                    var trimmed = line.Trim();

                    // 检测媒体部分
                    if (trimmed.StartsWith("m=video "))
                    {
                        foundVideo = true;
                        foundAudio = false;
                        videoOptimized = false;
                    }
                    else if (trimmed.StartsWith("m=audio "))
                    {
                        foundAudio = true;
                        foundVideo = false;
                        audioOptimized = false;
                    }
                    else if (trimmed.StartsWith("m="))
                    {
                        foundAudio = false;
                        foundVideo = false;
                    }

                    // 🎥 视频优化
                    if (foundVideo && !videoOptimized && trimmed.StartsWith("a=") &&
                        !trimmed.StartsWith("a=rtcp:") && trimmed.Length > 2)
                    {
                        if (!sdp.Contains("a=x-google-flag:low-latency"))
                            optimizedLines.Add("a=x-google-flag:low-latency");

                        if (!sdp.Contains("a=minBufferedPlaybackTime"))
                            optimizedLines.Add("a=minBufferedPlaybackTime:0");

                        // 启用 RTCP feedback、低延迟 H.264/H.265 编码模式
                        optimizedLines.Add("a=rtcp-fb:96 nack pli");
                        optimizedLines.Add("a=rtcp-fb:96 goog-remb");
                        optimizedLines.Add("a=rtcp-fb:96 transport-cc");
                        optimizedLines.Add("a=extmap-allow-mixed");
                        optimizedLines.Add("a=fmtp:96 packetization-mode=1;max-latency=0;profile-level-id=42001f");

                        videoOptimized = true;
                    }

                    // 🔊 音频优化
                    if (foundAudio && !audioOptimized && trimmed.StartsWith("a=") &&
                        !trimmed.StartsWith("a=rtcp:") && trimmed.Length > 2)
                    {
                        if (!sdp.Contains("a=minBufferedPlaybackTime"))
                            optimizedLines.Add("a=minBufferedPlaybackTime:0");

                        optimizedLines.Add("a=extmap-allow-mixed");
                        optimizedLines.Add("a=rtcp-fb:111 transport-cc");

                        audioOptimized = true;
                    }
                }

                var result = string.Join("\r\n", optimizedLines);

                // 验证完整性
                if (!result.Contains("v=0") || !result.Contains("m="))
                {
                    _logger.LogWarning("⚠️ SDP 优化后结构不完整，使用原始 SDP");
                    return sdp;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ SDP 优化失败，使用原始 SDP");
                return sdp;
            }
        }

        /// <summary>
        /// 将 SDP 中的候选地址和连接地址覆盖为配置的公网 IP（如果有）
        /// </summary>
        private string ApplyPublicIpToSdp(string sdp)
        {
            var publicIp = _config.PublicIp?.Trim();
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                return sdp;
            }

            try
            {
                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var updated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("c=IN IP", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length >= 3)
                        {
                            parts[2] = publicIp;
                            lines[i] = string.Join(" ", parts);
                            updated = true;
                        }
                    }
                    else if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
                    {
                        var segments = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length > 7 && string.Equals(segments[6], "typ", StringComparison.OrdinalIgnoreCase))
                        {
                            var candidateType = segments[7];
                            if (string.Equals(candidateType, "host", StringComparison.OrdinalIgnoreCase))
                            {
                                segments[4] = publicIp;
                                lines[i] = string.Join(" ", segments);
                                updated = true;
                            }
                        }
                    }
                }

                if (updated)
                {
                    _logger.LogInformation("🌐 已应用 WebRTC PublicIp 配置: {PublicIp}", publicIp);
                    return string.Join("\r\n", lines);
                }

                return sdp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 应用 PublicIp 到 SDP 失败，使用原始 SDP");
                return sdp;
            }
        }


        private string PrioritizeLanCandidates(string sdp, bool? preferLanCandidatesOverride = null)
        {
            var preferLanCandidates = preferLanCandidatesOverride ?? _config.PreferLanCandidates;

            if (!preferLanCandidates || string.IsNullOrWhiteSpace(sdp))
            {
                return sdp;
            }

            try
            {
                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var optimizedLines = new List<string>(lines.Length);
                var candidateBuffer = new List<(string line, int index)>();
                var collectingCandidates = false;
                var order = 0;

                void FlushBuffer()
                {
                    if (candidateBuffer.Count == 0) return;

                    var sorted = candidateBuffer
                        .Select(entry => new { entry.line, entry.index, score = ScoreCandidate(entry.line) })
                        .OrderByDescending(x => x.score)
                        .ThenBy(x => x.index)
                        .Select(x => x.line);

                    optimizedLines.AddRange(sorted);
                    candidateBuffer.Clear();
                }

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("m=", StringComparison.Ordinal))
                    {
                        FlushBuffer();
                        optimizedLines.Add(line);
                        collectingCandidates = false;
                        continue;
                    }

                    if (trimmed.StartsWith("a=candidate", StringComparison.Ordinal))
                    {
                        collectingCandidates = true;
                        candidateBuffer.Add((line, order++));
                        continue;
                    }

                    if (collectingCandidates && !trimmed.StartsWith("a=candidate", StringComparison.Ordinal))
                    {
                        FlushBuffer();
                        collectingCandidates = false;
                    }

                    optimizedLines.Add(line);
                }

                FlushBuffer();

                return string.Join("\r\n", optimizedLines);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 优化候选地址顺序失败，使用原始 SDP");
                return sdp;
            }
        }

        private int ScoreCandidate(string candidateLine)
        {
            if (string.IsNullOrWhiteSpace(candidateLine))
            {
                return 0;
            }

            var parts = candidateLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8)
            {
                return 0;
            }

            var protocol = parts[2].ToLowerInvariant();
            var address = parts[4];
            var component = parts[1];

            var typeIndex = Array.IndexOf(parts, "typ");
            var candidateType = typeIndex >= 0 && typeIndex + 1 < parts.Length
                ? parts[typeIndex + 1].ToLowerInvariant()
                : string.Empty;

            var score = 0;

            if (candidateType == "host" && IsPrivateAddress(address))
            {
                score += 400;
            }
            else if (candidateType == "host")
            {
                score += 320;
            }
            else if (candidateType == "srflx")
            {
                score += 200;
            }
            else if (candidateType == "prflx")
            {
                score += 150;
            }
            else if (candidateType == "relay")
            {
                score += 50;
            }

            if (protocol == "udp")
            {
                score += 40;
            }

            if (component == "1")
            {
                score += 10;
            }

            return score;
        }

        private static bool IsPrivateAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            if (IPAddress.TryParse(address, out var ip))
            {
                if (IPAddress.IsLoopback(ip))
                {
                    return true;
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var bytes = ip.GetAddressBytes();
                    return bytes[0] switch
                    {
                        10 => true,
                        172 when bytes.Length > 1 && bytes[1] >= 16 && bytes[1] <= 31 => true,
                        192 when bytes.Length > 1 && bytes[1] == 168 => true,
                        169 when bytes.Length > 1 && bytes[1] == 254 => true,
                        _ => false
                    };
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    var lower = ip.ToString().ToLowerInvariant();
                    return lower.StartsWith("fe80") || lower.StartsWith("fd") || lower.StartsWith("fc");
                }
            }
            else
            {
                var lowerAddress = address.ToLowerInvariant();
                if (lowerAddress.StartsWith("fe80") || lowerAddress.StartsWith("fd") || lowerAddress.StartsWith("fc"))
                {
                    return true;
                }
            }

            if (address.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理过期会话（超过 1 小时）
        /// </summary>
        public void CleanupExpiredSessions()
        {
            var expired = _sessions.Where(s =>
                (DateTime.UtcNow - s.Value.CreatedAt).TotalHours > 1
            ).ToList();

            foreach (var session in expired)
            {
                _logger.LogInformation("🧹 清理过期会话: {SessionId}", session.Key);
                RemoveSession(session.Key);
            }
        }
    }

    /// <summary>
    /// WebRTC 会话信息
    /// </summary>
    public class WebRTCSession
    {
        public required string SessionId { get; init; }
        public required RTCPeerConnection PeerConnection { get; init; }
        public required WebRTCReceiver Receiver { get; init; }
        public DateTime CreatedAt { get; init; }
        public Guid? StreamingSessionId { get; set; } // ✅ 关联的 Streaming Session ID（用于请求关键帧）
        public string? PreferredVideoCodec { get; init; }

        public RTCPeerConnectionState ConnectionState => PeerConnection.connectionState;
        public RTCIceConnectionState IceConnectionState => PeerConnection.iceConnectionState;
    }
}


