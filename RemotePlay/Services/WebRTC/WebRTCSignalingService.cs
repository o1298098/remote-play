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
                var config = new RTCConfiguration
                {
                    iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            },
                    bundlePolicy = RTCBundlePolicy.max_bundle,
                    rtcpMuxPolicy = RTCRtcpMuxPolicy.require,
                    iceTransportPolicy = RTCIceTransportPolicy.all
                };

                // 从 Settings 表读取 TURN 配置
                var turnServers = await GetTurnServersFromSettingsAsync();
                
                if (turnServers.Count > 0)
                {
                    int turnServerCount = 0;
                    foreach (var turn in turnServers.Where(t => !string.IsNullOrWhiteSpace(t.Url)))
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

                // 从数据库读取 WebRTC 配置（包括端口范围和 PublicIp）
                var webrtcConfig = await GetWebRTCConfigFromSettingsAsync();
                var portRange = CreatePortRange(webrtcConfig);
                
                if (portRange != null)
                {
                    _logger.LogInformation("🌐 使用数据库配置的 WebRTC 端口范围: {Min}-{Max} (Shuffle={Shuffle})",
                        webrtcConfig.IcePortMin, webrtcConfig.IcePortMax, webrtcConfig.ShufflePorts);
                }
                
                if (!string.IsNullOrWhiteSpace(webrtcConfig.PublicIp))
                {
                    _logger.LogInformation("🌐 使用数据库配置的 WebRTC PublicIp: {PublicIp}", webrtcConfig.PublicIp);
                }
                
                var peerConnection = new RTCPeerConnection(config, portRange: portRange);

                // 在 createOffer 之前创建 DataChannel，确保 SDP 中包含 m=application section
                RTCDataChannel? keepaliveDataChannel = null;
                try
                {
                    var dataChannelInit = new RTCDataChannelInit
                    {
                        ordered = true,
                        maxRetransmits = 0, // keepalive 不需要重传
                        maxPacketLifeTime = null
                    };
                    keepaliveDataChannel = await peerConnection.createDataChannel("keepalive", dataChannelInit);
                    _logger.LogInformation("✅ Keepalive DataChannel 已在 createOffer 前创建: {SessionId}", sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ 创建 Keepalive DataChannel 失败: {SessionId}，将使用备用方案", sessionId);
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
                    _logger.LogInformation("🔌 WebRTC 会话断开: {SessionId}", sessionId);
                    await RemoveSessionAsync(sessionId);
                };
                
                receiver.OnIceRestartRequested += async (s, e) =>
                {
                    _logger.LogInformation("🔄 收到 ICE Restart 请求: {SessionId}", sessionId);
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
                                _logger.LogInformation("✅ 请求关键帧成功: {SessionId}", session.StreamingSessionId.Value);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 请求关键帧失败");
                        }
                    }
                };

                var offer = peerConnection.createOffer();
                await peerConnection.setLocalDescription(offer);

                _logger.LogInformation("✅ 创建 WebRTC 会话: {SessionId}, 状态: {State}, ICE: {IceState}",
                    sessionId, peerConnection.connectionState, peerConnection.iceConnectionState);
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
                        catch { }
                        
                        if (candidateStr.Contains("typ relay"))
                        {
                            _logger.LogInformation("🌐 发现 TURN relay 候选地址: {Candidate}", candidate.candidate);
                        }
                        
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
                                _logger.LogInformation("📦 已存储 ICE candidate 供前端获取: SessionId={SessionId}",
                                    sessionId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ 存储 ICE candidate 失败: SessionId={SessionId}", sessionId);
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

                int waitTimeoutMs = hasTurnServers ? 8000 : 2000;
                
                await Task.WhenAny(tcs.Task, Task.Delay(waitTimeoutMs));

                if (!gatheringComplete)
                {
                    _logger.LogWarning("⚠️ ICE Gathering 未完成（等待{Timeout}ms），已收集 {Count} 个 candidates (host={Host}, srflx={Srflx}, relay={Relay})", 
                        waitTimeoutMs, candidateCount, hostCandidateCount, srflxCandidateCount, relayCandidateCount);
                    
                    if (hasTurnServers && relayCandidateCount == 0)
                    {
                        _logger.LogWarning("⚠️ 配置了TURN服务器但未收集到relay候选地址");
                    }
                }

                var finalSdp = OptimizeSdpForLowLatency(peerConnection.localDescription.sdp.ToString());
                finalSdp = ApplyPublicIpToSdp(finalSdp, webrtcConfig.PublicIp);
                finalSdp = PrioritizeLanCandidates(finalSdp, preferLanCandidatesOverride ?? webrtcConfig.PreferLanCandidates);

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
                var iceGatheringState = session.PeerConnection.iceGatheringState;

                if (result == SetDescriptionResultEnum.OK)
                {
                    _logger.LogInformation("✅ Answer 设置成功: SessionId={SessionId}, SignalingState={SignalingState}, ConnectionState={ConnectionState}, IceConnectionState={IceConnectionState}",
                        sessionId, signalingState, connectionState, iceState);
                    
                    if (signalingState != RTCSignalingState.stable)
                    {
                        _logger.LogWarning("⚠️ Answer 设置返回 OK，但信令状态是 {Signaling}，不是 stable", signalingState);
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
                             
                             _logger.LogInformation("📦 已存储 ICE candidate 供前端获取: SessionId={SessionId}",
                                 sessionId);

                            try
                            {
                                session.PeerConnection.addLocalIceCandidate(candidate);
                            }
                            catch { }
                        }
                    };
                    
                    session.PeerConnection.oniceconnectionstatechange += (state) =>
                    {
                        var currentIceState = session.PeerConnection.iceConnectionState;
                        var connectionState = session.PeerConnection.connectionState;
                        var signalingState = session.PeerConnection.signalingState;
                        
                        _logger.LogInformation("🧊 ICE 连接状态变化: SessionId={SessionId}, 状态: {IceConnectionState}, ConnectionState={ConnectionState}, SignalingState={SignalingState}",
                            sessionId, currentIceState, connectionState, signalingState);
                        
                        if (currentIceState == RTCIceConnectionState.@connected)
                        {
                            _logger.LogInformation("🎉 ICE 连接成功建立: SessionId={SessionId}", sessionId);
                        }
                        else if (currentIceState == RTCIceConnectionState.failed)
                        {
                            _logger.LogWarning("❌ ICE 连接失败: SessionId={SessionId}, ConnectionState={ConnectionState}, SignalingState={SignalingState}",
                                sessionId, connectionState, signalingState);
                            
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000); // 等待 10 秒
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
                            _logger.LogWarning("⚠️ ICE 连接断开: SessionId={SessionId}，将在延迟后尝试 ICE Restart", sessionId);
                            
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000); // 等待 10 秒
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
                        
                        _logger.LogInformation("🔌 WebRTC 连接状态变化: SessionId={SessionId}, 状态: {ConnectionState}, IceConnectionState={IceConnectionState}, SignalingState={SignalingState}",
                            sessionId, currentConnectionState, iceConnectionState, signalingState);
                        
                        if (currentConnectionState == RTCPeerConnectionState.@connected)
                        {
                            _logger.LogInformation("🎉 WebRTC 连接成功建立: SessionId={SessionId}", sessionId);
                        }
                        else if (currentConnectionState == RTCPeerConnectionState.failed)
                        {
                            _logger.LogWarning("❌ WebRTC 连接失败: SessionId={SessionId}, IceConnectionState={IceConnectionState}, SignalingState={SignalingState}",
                                sessionId, iceConnectionState, signalingState);
                        }
                    };

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
        /// 获取会话中待处理的 ICE Candidate（后端生成的新 candidate）
        /// </summary>
        public List<RTCIceCandidateInit> GetPendingIceCandidates(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var webrtcSession))
            {
                _logger.LogWarning("⚠️ 会话不存在: {SessionId}", sessionId);
                return new List<RTCIceCandidateInit>();
            }

            var allCandidates = webrtcSession.GetPendingIceCandidates();
            
            string? frontendUfrag = null;
            try
            {
                var remoteDescription = webrtcSession.PeerConnection.remoteDescription;
                if (remoteDescription?.sdp != null)
                {
                    var sdp = remoteDescription.sdp.ToString();
                    frontendUfrag = ExtractIceUfragFromSdp(sdp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 提取前端 ufrag 失败，将返回所有 candidate");
            }
            
            List<RTCIceCandidateInit> filteredCandidates;
            if (!string.IsNullOrWhiteSpace(frontendUfrag))
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
                        return candidateUfrag == frontendUfrag;
                    }
                    
                    return true;
                }).ToList();
            }
            else
            {
                filteredCandidates = allCandidates;
                _logger.LogWarning("⚠️ 无法提取前端 ufrag，返回所有 {Count} 个 candidate", allCandidates.Count);
            }
            
            if (filteredCandidates.Count > 0)
            {
                _logger.LogInformation("📤 返回 {Count} 个待处理的 ICE candidate 给前端: SessionId={SessionId}",
                    filteredCandidates.Count, sessionId);
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
                _logger.LogWarning("⚠️ 会话不存在: {SessionId}", sessionId);
                return false;
            }

            try
            {
                var iceCandidate = new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid,
                    sdpMLineIndex = sdpMLineIndex
                };

                session.PeerConnection.addIceCandidate(iceCandidate);
                
                _logger.LogInformation("✅ ICE Candidate 已添加到 PeerConnection: SessionId={SessionId}",
                    sessionId);
                
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
                            var stopped = await _streamingService.StopStreamAsync(session.StreamingSessionId.Value);
                            if (stopped)
                            {
                                _logger.LogInformation("✅ 流会话已停止: {StreamingSessionId}", session.StreamingSessionId.Value);
                            }
                        }
                        catch (Exception streamEx)
                        {
                            _logger.LogWarning(streamEx, "⚠️ 停止流会话时出错: {StreamingSessionId}", session.StreamingSessionId.Value);
                        }
                    }

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
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析 WebRTC 配置 JSON 失败，使用默认配置");
                    }
                }

                // 如果数据库中没有 TurnServers，尝试从单独的 TURN 配置读取
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
                _logger.LogWarning(ex, "⚠️ 从 Settings 表读取 WebRTC 配置失败，使用默认配置");
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
                _logger.LogWarning(ex, "⚠️ 从 Settings 表读取 TURN 配置失败，使用空配置");
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






