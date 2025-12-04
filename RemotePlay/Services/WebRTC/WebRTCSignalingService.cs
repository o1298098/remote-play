using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Statistics;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Configuration;
using RemotePlay.Models.Context;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace RemotePlay.Services.WebRTC
{
    /// <summary>
    /// WebRTC 信令服务 - 管理 WebRTC 连接和信令交换
    /// </summary>
    public partial class WebRTCSignalingService
    {
        private readonly ILogger<WebRTCSignalingService> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, WebRTCSession> _sessions;
        private readonly LatencyStatisticsService? _latencyStats;
        private readonly IControllerService? _controllerService;
        private readonly IStreamingService? _streamingService;
        private readonly WebRTCConfig _config;
        private readonly PortRange? _portRange;
        private readonly IServiceProvider _serviceProvider;
        private const string TurnConfigKey = "webrtc.turn_servers";
        private const string WebRTCConfigKey = "webrtc.config";

        public WebRTCSignalingService(
            ILogger<WebRTCSignalingService> logger,
            ILoggerFactory loggerFactory,
            LatencyStatisticsService? latencyStats = null,
            IControllerService? controllerService = null,
            IStreamingService? streamingService = null,
            IOptions<WebRTCConfig>? webrtcOptions = null,
            IServiceProvider? serviceProvider = null)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _latencyStats = latencyStats;
            _controllerService = controllerService;
            _streamingService = streamingService;
            _sessions = new ConcurrentDictionary<string, WebRTCSession>();
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            _config = webrtcOptions?.Value ?? new WebRTCConfig();
            _portRange = CreatePortRange(_config);

            if (_portRange != null)
            {
                _logger.LogInformation("WebRTC 自定义端口范围: {Min}-{Max} (Shuffle={Shuffle})",
                    _config.IcePortMin,
                    _config.IcePortMax,
                    _config.ShufflePorts);
            }
            else if (_config.IcePortMin.HasValue || _config.IcePortMax.HasValue)
            {
                _logger.LogWarning("WebRTC 端口范围配置无效，将回退为系统随机端口 (min={Min}, max={Max})",
                    _config.IcePortMin,
                    _config.IcePortMax);
            }
        }

        /// <summary>
        /// 创建新的 WebRTC 会话
        /// </summary>
        public async Task<(string sessionId, string offer)> CreateSessionAsync(
            string? preferredVideoCodec = null,
            bool? preferLanCandidatesOverride = null,
            bool? forceUseTurnForTest = null)
        {
            var sessionId = Guid.NewGuid().ToString("N");

            try
            {
                var webrtcConfig = await GetWebRTCConfigFromSettingsAsync();
                var turnServers = await GetTurnServersFromSettingsAsync();
                var iceTransportPolicy = RTCIceTransportPolicy.all;
                var shouldForceUseTurn = forceUseTurnForTest ?? webrtcConfig.ForceUseTurn;
                if (shouldForceUseTurn)
                {
                    if (turnServers.Count > 0)
                    {
                        iceTransportPolicy = RTCIceTransportPolicy.relay;
                    }
                    else
                    {
                        _logger.LogWarning("启用了 ForceUseTurn 但未配置 TURN 服务器，将回退为 all: SessionId={SessionId}", sessionId);
                    }
                }
                
                var config = new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer>
                    {
                        new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                    },
                    bundlePolicy = RTCBundlePolicy.max_bundle,
                    rtcpMuxPolicy = RTCRtcpMuxPolicy.require,
                    iceTransportPolicy = iceTransportPolicy
                };
                
                if (turnServers.Count > 0)
                {
                    foreach (var turn in turnServers.Where(t => !string.IsNullOrWhiteSpace(t.Url)))
                    {
                        config.iceServers.Add(new RTCIceServer
                        {
                            urls = turn.Url!,
                            username = turn.Username,
                            credential = turn.Credential
                        });
                    }
                }
                
                var portRange = CreatePortRange(webrtcConfig);
                
                var peerConnection = new RTCPeerConnection(config, portRange: portRange);

                RTCDataChannel? keepaliveDataChannel = null;
                try
                {
                    var dataChannelInit = new RTCDataChannelInit
                    {
                        ordered = true,
                        maxRetransmits = 0,
                        maxPacketLifeTime = null
                    };
                    keepaliveDataChannel = await peerConnection.createDataChannel("keepalive", dataChannelInit);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "创建 Keepalive DataChannel 失败: {SessionId}", sessionId);
                }
                
                var receiver = new WebRTCReceiver(
                    sessionId,
                    peerConnection,
                    _loggerFactory.CreateLogger<WebRTCReceiver>(),
                    _latencyStats,
                    preferredVideoCodec
                );
                
                if (keepaliveDataChannel != null)
                {
                    receiver.SetKeepaliveDataChannel(keepaliveDataChannel);
                }

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

                receiver.OnDisconnected += async (s, e) =>
                {
                    await RemoveSessionAsync(sessionId);
                };
                
                receiver.OnIceRestartRequested += async (s, e) =>
                {
                    await TryIceRestartAsync(sessionId);
                };

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
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "请求关键帧失败");
                        }
                    }
                };

                var offer = peerConnection.createOffer();
                await peerConnection.setLocalDescription(offer);

                var tcs = new TaskCompletionSource<bool>();
                int candidateCount = 0;
                int hostCandidateCount = 0;
                int srflxCandidateCount = 0;
                int relayCandidateCount = 0;
                bool gatheringComplete = false;
                bool hasTurnServers = turnServers.Count > 0;
                bool isForceUseTurn = webrtcConfig.ForceUseTurn && hasTurnServers;
                var startTime = DateTime.UtcNow;
                const int minWaitTimeForTurnMs = 2000;

                bool CheckAndCompleteEarly()
                {
                    if (!hasTurnServers || gatheringComplete || relayCandidateCount < 1)
                        return false;

                    var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    if (elapsedMs < minWaitTimeForTurnMs)
                        return false;

                    bool canEarlyComplete = elapsedMs >= minWaitTimeForTurnMs && relayCandidateCount >= 1;

                    if (canEarlyComplete)
                    {
                        gatheringComplete = true;
                        tcs.TrySetResult(true);
                        return true;
                    }

                    return false;
                }

                peerConnection.onicecandidate += (candidate) =>
                {
                    if (candidate != null)
                    {
                        candidateCount++;
                        
                        var candidateStr = candidate.candidate?.ToLowerInvariant() ?? "";
                        if (candidateStr.Contains("typ host"))
                            hostCandidateCount++;
                        else if (candidateStr.Contains("typ srflx"))
                            srflxCandidateCount++;
                        else if (candidateStr.Contains("typ relay"))
                        {
                            relayCandidateCount++;
                            CheckAndCompleteEarly();
                        }
                        
                        try { peerConnection.addLocalIceCandidate(candidate); }
                        catch { }
                        
                        try
                        {
                            if (_sessions.TryGetValue(sessionId, out var existingSession))
                            {
                                var candidateWithUfrag = EnsureCandidateHasUfrag(candidate.candidate, existingSession.PeerConnection);
                                
                                existingSession.AddPendingIceCandidate(new RTCIceCandidateInit
                                {
                                    candidate = candidateWithUfrag,
                                    sdpMid = candidate.sdpMid,
                                    sdpMLineIndex = candidate.sdpMLineIndex
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "存储 ICE candidate 失败: SessionId={SessionId}", sessionId);
                        }
                    }
                    else
                    {
                        gatheringComplete = true;
                        tcs.TrySetResult(true);
                    }
                };

                int baseTimeoutMs = isForceUseTurn ? 15000 : (hasTurnServers ? 10000 : 2000);
                int waitTimeoutMs = baseTimeoutMs + (turnServers.Count * 3000);
                
                if (hasTurnServers)
                {
                    const int checkIntervalMs = 500;
                    var remainingTime = waitTimeoutMs;
                    
                    while (!gatheringComplete && remainingTime > 0)
                    {
                        var checkDelay = Math.Min(checkIntervalMs, remainingTime);
                        await Task.WhenAny(tcs.Task, Task.Delay(checkDelay));
                        
                        if (tcs.Task.IsCompleted)
                        {
                            break;
                        }
                        
                        if (CheckAndCompleteEarly())
                        {
                            break;
                        }
                        
                        remainingTime -= checkDelay;
                    }
                }
                else
                {
                    await Task.WhenAny(tcs.Task, Task.Delay(waitTimeoutMs));
                }

                if (!gatheringComplete)
                {
                    _logger.LogWarning("ICE Gathering 未完成（等待{Timeout}ms），已收集 {Count} 个 candidates (host={Host}, srflx={Srflx}, relay={Relay})", 
                        waitTimeoutMs, candidateCount, hostCandidateCount, srflxCandidateCount, relayCandidateCount);
                    
                    if (hasTurnServers)
                    {
                        if (relayCandidateCount == 0)
                        {
                            _logger.LogWarning("配置了 {TurnCount} 个 TURN 服务器但未收集到任何 relay 候选地址", turnServers.Count);
                        }
                        else if (relayCandidateCount < turnServers.Count)
                        {
                            _logger.LogWarning("配置了 {TurnCount} 个 TURN 服务器，但只收集到 {RelayCount} 个 relay 候选地址", 
                                turnServers.Count, relayCandidateCount);
                        }
                        
                        if (isForceUseTurn && relayCandidateCount == 0)
                        {
                            _logger.LogError("强制使用 TURN 模式，但未收集到任何 relay 候选地址，这会导致 ICE 连接失败");
                            _logger.LogError("可能的原因：TURN 服务器 URL/用户名/密码错误、无法访问、不支持指定协议或等待时间不足");
                            _logger.LogError("已配置的 TURN 服务器数量: {Count}", turnServers.Count);
                            foreach (var turn in turnServers)
                            {
                                _logger.LogError("   - URL: {Url}, 用户名: {Username}", 
                                    turn.Url, string.IsNullOrWhiteSpace(turn.Username) ? "无" : "已设置");
                            }
                        }
                    }
                }
                else if (isForceUseTurn && relayCandidateCount == 0)
                {
                    _logger.LogError("强制使用 TURN 模式，但 ICE Gathering 完成后仍未收集到任何 relay 候选地址");
                }
                else if (isForceUseTurn && relayCandidateCount < turnServers.Count)
                {
                    _logger.LogWarning("强制使用 TURN 模式，ICE Gathering 完成但只收集到 {RelayCount}/{TurnCount} 个 relay 候选地址", 
                        relayCandidateCount, turnServers.Count);
                }

                var finalSdp = OptimizeSdpForLowLatency(peerConnection.localDescription.sdp.ToString());
                finalSdp = ApplyPublicIpToSdp(finalSdp, webrtcConfig.PublicIp);
                finalSdp = PrioritizeLanCandidates(finalSdp, preferLanCandidatesOverride ?? webrtcConfig.PreferLanCandidates);

                return (sessionId, finalSdp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 WebRTC 会话失败");
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
                    _logger.LogWarning("Answer SDP 中没有找到 m=video 行");
                }

                if (!answerSdp.Contains("m=audio"))
                {
                    _logger.LogWarning("Answer SDP 中没有找到 m=audio 行");
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
                var iceGatheringState = session.PeerConnection.iceGatheringState;

                if (result == SetDescriptionResultEnum.OK)
                {
                    if (signalingState != RTCSignalingState.stable)
                    {
                        _logger.LogWarning("Answer 设置返回 OK，但信令状态是 {Signaling}，不是 stable", signalingState);
                    }
                    
                    try
                    {
                        session.Receiver.InitializeVideoPipelineEarly();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "提前初始化视频管道失败: SessionId={SessionId}", sessionId);
                    }
                    
                    session.PeerConnection.onicecandidate += (candidate) =>
                    {
                        if (candidate != null && candidate.candidate != null)
                        {
                            
                            var candidateWithUfrag = EnsureCandidateHasUfrag(candidate.candidate, session.PeerConnection);
                             
                             session.AddPendingIceCandidate(new RTCIceCandidateInit
                             {
                                 candidate = candidateWithUfrag,
                                 sdpMid = candidate.sdpMid,
                                 sdpMLineIndex = candidate.sdpMLineIndex
                             });

                            try
                            {
                                session.PeerConnection.addLocalIceCandidate(candidate);
                            }
                            catch { }
                        }
                    };
                    
                    if (session.PeerConnection.iceGatheringState != RTCIceGatheringState.complete)
                    {
                        _ = Task.Run(async () =>
                        {
                            var startTime = DateTime.UtcNow;
                            var maxWaitTime = TimeSpan.FromSeconds(5);
                            
                            while (DateTime.UtcNow - startTime < maxWaitTime)
                            {
                                if (session.PeerConnection.iceGatheringState == RTCIceGatheringState.complete)
                                {
                                    break;
                                }
                                await Task.Delay(200);
                            }
                        });
                    }
                    
                    session.PeerConnection.oniceconnectionstatechange += (state) =>
                    {
                        var currentIceState = session.PeerConnection.iceConnectionState;
                        var connectionState = session.PeerConnection.connectionState;
                        var signalingState = session.PeerConnection.signalingState;
                        
                        if (currentIceState == RTCIceConnectionState.failed)
                        {
                            var iceGatheringState = session.PeerConnection.iceGatheringState;
                            var config = session.PeerConnection.getConfiguration();
                            var hasTurnServers = config?.iceServers?.Any(s => 
                                s.urls != null && s.urls.Contains("turn:", StringComparison.OrdinalIgnoreCase)) ?? false;
                            
                            _logger.LogWarning("ICE 连接失败: SessionId={SessionId}, ConnectionState={ConnectionState}, SignalingState={SignalingState}, IceGatheringState={IceGatheringState}, HasTurnServers={HasTurnServers}",
                                sessionId, connectionState, signalingState, iceGatheringState, hasTurnServers);
                            
                            if (hasTurnServers)
                            {
                                _logger.LogWarning("TURN 服务器已配置但 ICE 连接失败，可能的原因：TURN 服务器无法访问、用户名/密码错误、候选地址不匹配、不支持指定协议或强制 TURN 模式配置问题");
                            }
                            
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000);
                                if (_sessions.ContainsKey(sessionId))
                                {
                                    var session = _sessions[sessionId];
                                    if (session.PeerConnection.iceConnectionState == RTCIceConnectionState.failed ||
                                        session.PeerConnection.iceConnectionState == RTCIceConnectionState.disconnected)
                                    {
                                        await TryIceRestartAsync(sessionId);
                                    }
                                }
                            });
                        }
                        else if (currentIceState == RTCIceConnectionState.disconnected)
                        {
                            _logger.LogWarning("ICE 连接断开: SessionId={SessionId}，将在延迟后尝试 ICE Restart", sessionId);
                            
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000);
                                if (_sessions.ContainsKey(sessionId))
                                {
                                    var session = _sessions[sessionId];
                                    if (session.PeerConnection.iceConnectionState == RTCIceConnectionState.disconnected ||
                                        session.PeerConnection.iceConnectionState == RTCIceConnectionState.failed)
                                    {
                                        await TryIceRestartAsync(sessionId);
                                    }
                                }
                            });
                        }
                    };
                    
                    session.PeerConnection.onconnectionstatechange += (state) =>
                    {
                        var currentConnectionState = session.PeerConnection.connectionState;
                        var iceConnectionState = session.PeerConnection.iceConnectionState;
                        var signalingState = session.PeerConnection.signalingState;
                        
                        if (currentConnectionState == RTCPeerConnectionState.failed)
                        {
                            _logger.LogWarning("WebRTC 连接失败: SessionId={SessionId}, IceConnectionState={IceConnectionState}, SignalingState={SignalingState}",
                                sessionId, iceConnectionState, signalingState);
                        }
                    };

                    return true;
                }
                else
                {
                    _logger.LogWarning("设置 Answer 返回非 OK 状态: {SessionId}, 结果: {Result}", sessionId, result);

                    if (result == SetDescriptionResultEnum.VideoIncompatible)
                    {
                        if (signalingState == RTCSignalingState.have_remote_pranswer ||
                            signalingState == RTCSignalingState.stable)
                        {
                            _logger.LogWarning("视频不兼容，但信令状态已改变为 {Signaling}，允许连接继续", signalingState);
                            return true;
                        }
                        else
                        {
                            _logger.LogError("视频不兼容且 Answer 未被设置，信令状态: {Signaling}", signalingState);
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
                                        _logger.LogError("浏览器 Answer 中只包含 telephone-event，没有 Opus");
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
                                        _logger.LogError(forceEx, "强制设置 remote description 失败");
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
                                _logger.LogError(ex, "检查 remote description 时出错");
                                return true;
                            }
                        }
                    }

                    _logger.LogError("设置 Answer 失败: {SessionId}, 结果: {Result}", sessionId, result);
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
        /// 获取会话中待处理的 ICE Candidate（后端生成的新 candidate）
        /// </summary>
        public List<RTCIceCandidateInit> GetPendingIceCandidates(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var webrtcSession))
            {
                _logger.LogWarning("会话不存在: {SessionId}", sessionId);
                return new List<RTCIceCandidateInit>();
            }

            var allCandidates = webrtcSession.GetPendingIceCandidates();
            
            string? backendUfrag = null;
            try
            {
                var localDescription = webrtcSession.PeerConnection.localDescription;
                if (localDescription?.sdp != null)
                {
                    var sdp = localDescription.sdp.ToString();
                    backendUfrag = ExtractIceUfragFromSdp(sdp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "提取后端 ufrag 失败，将返回所有 candidate");
            }
            
            List<RTCIceCandidateInit> filteredCandidates;
            if (!string.IsNullOrWhiteSpace(backendUfrag))
            {
                filteredCandidates = allCandidates.Where(c =>
                {
                    if (string.IsNullOrWhiteSpace(c.candidate))
                    {
                        return false;
                    }
                    
                    var match = System.Text.RegularExpressions.Regex.Match(c.candidate, @"ufrag\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var candidateUfrag = match.Groups[1].Value;
                        return candidateUfrag == backendUfrag;
                    }
                    
                    return true;
                }).ToList();
            }
            else
            {
                filteredCandidates = allCandidates;
                _logger.LogWarning("无法提取后端 ufrag，返回所有 {Count} 个 candidate", allCandidates.Count);
            }
            
            
            return filteredCandidates;
        }

        /// <summary>
        /// 添加 ICE Candidate
        /// </summary>
        public async Task<bool> AddIceCandidateAsync(string sessionId, string candidate, string sdpMid, ushort sdpMLineIndex)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("会话不存在: {SessionId}", sessionId);
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    _logger.LogWarning("收到空的 ICE Candidate: SessionId={SessionId}", sessionId);
                    return false;
                }
                
                var candidateLower = candidate.ToLowerInvariant();
                
                if (candidateLower.Contains("ufrag"))
                {
                    var frontendUfragMatch = System.Text.RegularExpressions.Regex.Match(candidate, @"ufrag\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (frontendUfragMatch.Success)
                    {
                        var frontendUfrag = frontendUfragMatch.Groups[1].Value;
                        var answerUfrag = ExtractIceUfragFromSdp(session.PeerConnection.remoteDescription?.sdp?.ToString() ?? "");
                        
                        if (!string.IsNullOrWhiteSpace(answerUfrag) && frontendUfrag != answerUfrag)
                        {
                            _logger.LogWarning("前端 candidate 的 ufrag ({FrontendUfrag}) 与 Answer SDP 的 ufrag ({AnswerUfrag}) 不匹配，已自动修正",
                                frontendUfrag, answerUfrag);
                            
                            candidate = System.Text.RegularExpressions.Regex.Replace(
                                candidate, 
                                @"ufrag\s+\w+", 
                                $"ufrag {answerUfrag}", 
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    }
                }
                
                var iceCandidate = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid,
                    sdpMLineIndex = sdpMLineIndex
                };

                session.PeerConnection.addIceCandidate(iceCandidate);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加 ICE Candidate 失败: SessionId={SessionId}, Candidate={Candidate}", 
                    sessionId, candidate?.Length > 200 ? candidate.Substring(0, 200) + "..." : candidate);
                
                if (ex.Message.Contains("InvalidStateError") || ex.Message.Contains("InvalidState"))
                {
                    _logger.LogWarning("PeerConnection 状态可能不正确: ConnectionState={ConnectionState}, SignalingState={SignalingState}, IceConnectionState={IceConnectionState}",
                        session.PeerConnection.connectionState, session.PeerConnection.signalingState, session.PeerConnection.iceConnectionState);
                }
                
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
            _iceRestartAttempts.TryRemove(sessionId, out _);
            _iceRestartLastAttempt.TryRemove(sessionId, out _);
            _iceRestartPendingOfferTime.TryRemove(sessionId, out _);
            
            if (_iceRestartLocks.TryRemove(sessionId, out var sessionLock))
            {
                try
                {
                    sessionLock.Dispose();
                }
                catch { }
            }
            
            if (_sessions.TryRemove(sessionId, out var session))
            {
                try
                {
                    if (_streamingService != null && session.StreamingSessionId.HasValue)
                    {
                        try
                        {
                            await _streamingService.StopStreamAsync(session.StreamingSessionId.Value);
                        }
                        catch (Exception streamEx)
                        {
                            _logger.LogWarning(streamEx, "停止流会话时出错: {StreamingSessionId}", session.StreamingSessionId.Value);
                        }
                    }

                    if (_controllerService != null && Guid.TryParse(sessionId, out var sessionGuid))
                    {
                        try
                        {
                            await _controllerService.DisconnectAsync(sessionGuid);
                        }
                        catch (Exception controllerEx)
                        {
                            _logger.LogWarning(controllerEx, "断开控制器时出错: {SessionId}", sessionId);
                        }
                    }

                    session.Receiver?.Dispose();
                    session.PeerConnection?.close();
                    session.PeerConnection?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "移除会话时出错: {SessionId}", sessionId);
                }
            }
        }

        /// <summary>
        /// 移除会话（同步版本，向后兼容）
        /// </summary>
        public void RemoveSession(string sessionId)
        {
            _ = RemoveSessionAsync(sessionId);
        }
        
        /// <summary>
        /// 从 Settings 表读取完整的 WebRTC 配置（包括 PublicIp, IcePortMin, IcePortMax, TurnServers）
        /// </summary>
        private async Task<WebRTCConfig> GetWebRTCConfigFromSettingsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RPContext>();
                
                // 从数据库读取 WebRTC 配置
                var setting = await context.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == WebRTCConfigKey)
                    .FirstOrDefaultAsync();

                var result = new WebRTCConfig
                {
                    // 默认使用 appsettings.json 中的配置
                    PublicIp = _config.PublicIp,
                    IcePortMin = _config.IcePortMin,
                    IcePortMax = _config.IcePortMax,
                    ShufflePorts = _config.ShufflePorts,
                    PreferLanCandidates = _config.PreferLanCandidates,
                    TurnServers = new List<TurnServerConfig>()
                };

                if (setting != null)
                {
                    try
                    {
                        JObject? jsonObj = null;
                        
                        // 优先从 ValueJson 字段读取
                        if (setting.ValueJson != null)
                        {
                            jsonObj = setting.ValueJson;
                        }
                        // 如果 ValueJson 为空，尝试从 Value 字段解析 JSON
                        else if (!string.IsNullOrWhiteSpace(setting.Value))
                        {
                            jsonObj = JObject.Parse(setting.Value);
                        }

                        if (jsonObj != null)
                        {
                            // 解析 PublicIp
                            if (jsonObj["publicIp"] != null)
                                result.PublicIp = jsonObj["publicIp"]?.ToString();
                            else if (jsonObj["PublicIp"] != null)
                                result.PublicIp = jsonObj["PublicIp"]?.ToString();

                            // 解析 IcePortMin
                            if (jsonObj["icePortMin"] != null && jsonObj["icePortMin"].Type == JTokenType.Integer)
                                result.IcePortMin = jsonObj["icePortMin"].Value<int>();
                            else if (jsonObj["IcePortMin"] != null && jsonObj["IcePortMin"].Type == JTokenType.Integer)
                                result.IcePortMin = jsonObj["IcePortMin"].Value<int>();

                            // 解析 IcePortMax
                            if (jsonObj["icePortMax"] != null && jsonObj["icePortMax"].Type == JTokenType.Integer)
                                result.IcePortMax = jsonObj["icePortMax"].Value<int>();
                            else if (jsonObj["IcePortMax"] != null && jsonObj["IcePortMax"].Type == JTokenType.Integer)
                                result.IcePortMax = jsonObj["IcePortMax"].Value<int>();

                            // 解析 TurnServers
                            var serversToken = jsonObj["turnServers"] ?? jsonObj["TurnServers"] ?? jsonObj["servers"];
                            if (serversToken != null && serversToken.Type == JTokenType.Array)
                            {
                                var turnServers = new List<TurnServerConfig>();
                                foreach (var serverToken in serversToken)
                                {
                                    if (serverToken.Type != JTokenType.Object)
                                        continue;

                                    var serverObj = (JObject)serverToken;
                                    var url = serverObj["url"]?.ToString() ?? serverObj["Url"]?.ToString() ?? serverObj["urls"]?.ToString();
                                    if (string.IsNullOrWhiteSpace(url))
                                        continue;

                                    turnServers.Add(new TurnServerConfig
                                    {
                                        Url = url,
                                        Username = serverObj["username"]?.ToString() ?? serverObj["Username"]?.ToString(),
                                        Credential = serverObj["credential"]?.ToString() ?? serverObj["Credential"]?.ToString()
                                    });
                                }
                                result.TurnServers = turnServers;
                            }

                            // 解析 ForceUseTurn
                            var forceUseTurnToken = jsonObj["forceUseTurn"] ?? jsonObj["ForceUseTurn"];
                            if (forceUseTurnToken != null && forceUseTurnToken.Type == JTokenType.Boolean)
                            {
                                result.ForceUseTurn = forceUseTurnToken.Value<bool>();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析 WebRTC 配置 JSON 失败，使用默认配置");
                    }
                }

                if (result.TurnServers.Count == 0)
                {
                    var turnServers = await GetTurnServersFromSettingsAsync();
                    if (turnServers.Count > 0)
                    {
                        result.TurnServers = turnServers;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从 Settings 表读取 WebRTC 配置失败，使用默认配置");
                return new WebRTCConfig
                {
                    PublicIp = _config.PublicIp,
                    IcePortMin = _config.IcePortMin,
                    IcePortMax = _config.IcePortMax,
                    ShufflePorts = _config.ShufflePorts,
                    PreferLanCandidates = _config.PreferLanCandidates,
                    TurnServers = new List<TurnServerConfig>()
                };
            }
        }

        /// <summary>
        /// 从 Settings 表读取 TURN 服务器配置
        /// </summary>
        private async Task<List<TurnServerConfig>> GetTurnServersFromSettingsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RPContext>();
                
                var setting = await context.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == TurnConfigKey)
                    .FirstOrDefaultAsync();

                if (setting == null)
                {
                    return new List<TurnServerConfig>();
                }

                JObject? jsonObj = null;
                
                // 优先从 ValueJson 字段读取
                if (setting.ValueJson != null)
                {
                    jsonObj = setting.ValueJson;
                }
                // 如果 ValueJson 为空，尝试从 Value 字段解析 JSON
                else if (!string.IsNullOrWhiteSpace(setting.Value))
                {
                    jsonObj = JObject.Parse(setting.Value);
                }

                if (jsonObj == null)
                {
                    return new List<TurnServerConfig>();
                }

                var turnServers = new List<TurnServerConfig>();
                var serversToken = jsonObj["turnServers"] ?? jsonObj["TurnServers"] ?? jsonObj["servers"];
                
                if (serversToken != null && serversToken.Type == JTokenType.Array)
                {
                    foreach (var serverToken in serversToken)
                    {
                        if (serverToken.Type != JTokenType.Object)
                        {
                            continue;
                        }

                        var serverObj = (JObject)serverToken;
                        var url = serverObj["url"]?.ToString() ?? serverObj["Url"]?.ToString() ?? serverObj["urls"]?.ToString();
                        
                        if (string.IsNullOrWhiteSpace(url))
                        {
                            continue;
                        }

                        turnServers.Add(new TurnServerConfig
                        {
                            Url = url,
                            Username = serverObj["username"]?.ToString() ?? serverObj["Username"]?.ToString(),
                            Credential = serverObj["credential"]?.ToString() ?? serverObj["Credential"]?.ToString()
                        });
                    }
                }

                return turnServers;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从 Settings 表读取 TURN 配置失败，使用空配置");
                return new List<TurnServerConfig>();
            }
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

}






