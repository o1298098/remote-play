using Microsoft.AspNetCore.Mvc;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
using RemotePlay.Models.WebRTC;
using RemotePlay.Services.WebRTC;
using RemotePlay.Services.Statistics;
using RemotePlay.Services.Controller;
using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Launch;
using SIPSorcery.Net;

namespace RemotePlay.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebRTCController : ControllerBase
    {
        private readonly ILogger<WebRTCController> _logger;
        private readonly WebRTCSignalingService _signalingService;
        private readonly IStreamingService _streamingService;
        private readonly LatencyStatisticsService? _latencyStats;
        private readonly IControllerService? _controllerService;
        
        public WebRTCController(
            ILogger<WebRTCController> logger,
            WebRTCSignalingService signalingService,
            IStreamingService streamingService,
            LatencyStatisticsService? latencyStats = null,
            IControllerService? controllerService = null)
        {
            _logger = logger;
            _signalingService = signalingService;
            _streamingService = streamingService;
            _latencyStats = latencyStats;
            _controllerService = controllerService;
        }
        
        /// <summary>
        /// 创建新的 WebRTC 会话并返回 SDP Offer
        /// </summary>
        [HttpPost("offer")]
        public async Task<ActionResult<ResponseModel>> CreateOffer([FromBody] WebRTCOfferRequest? request = null)
        {
            try
            {
                string? preferredCodec = null;
                bool? preferLanCandidates = request?.PreferLanCandidates;

                if (request?.RemotePlaySessionId != null)
                {
                    var remoteSession = await _streamingService.GetSessionAsync(request.RemotePlaySessionId.Value);
                    if (remoteSession != null)
                    {
                        var launchOptions = remoteSession.LaunchOptions ?? StreamLaunchOptionsResolver.Resolve(remoteSession);
                        preferredCodec = launchOptions.VideoCodec;
                    }
                }

                var (sessionId, offer) = await _signalingService.CreateSessionAsync(preferredCodec, preferLanCandidates);
                
                _logger.LogInformation("🎯 WebRTC Offer 已创建: {SessionId}", sessionId);
                
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new WebRTCOfferResponse
                    {
                        SessionId = sessionId,
                        Sdp = offer,
                        Type = "offer"
                    },
                    Message = "WebRTC Offer 创建成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 创建 WebRTC Offer 失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "创建 WebRTC 会话失败",
                    ErrorCode = ErrorCode.WebRtcOfferCreationFailed
                });
            }
        }
        
        /// <summary>
        /// 接收客户端的 SDP Answer
        /// </summary>
        [HttpPost("answer")]
        public async Task<ActionResult<ResponseModel>> ReceiveAnswer([FromBody] WebRTCAnswerRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SessionId 不能为空",
                        ErrorCode = ErrorCode.SessionIdRequired
                    });
                }
                
                if (string.IsNullOrWhiteSpace(request.Sdp))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SDP 不能为空",
                        ErrorCode = ErrorCode.SdpRequired
                    });
                }
                
                _logger.LogInformation("📥 收到 Answer 请求: SessionId={SessionId}, SDP长度={Length}", 
                    request.SessionId, request.Sdp?.Length ?? 0);
                
                // ⚠️ 检查 SDP 是否为空
                if (string.IsNullOrWhiteSpace(request.Sdp))
                {
                    _logger.LogError("❌ Answer SDP 为空！");
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Answer SDP 为空",
                        ErrorCode = ErrorCode.AnswerSdpRequired
                    });
                }
                
                // ⚠️ 检查会话是否存在
                var sessionExists = _signalingService.GetSession(request.SessionId) != null;
                if (!sessionExists)
                {
                    _logger.LogError("❌ WebRTC 会话不存在: {SessionId}", request.SessionId);
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "WebRTC 会话不存在",
                        ErrorCode = ErrorCode.WebRtcSessionNotFound
                    });
                }
                
                var success = await _signalingService.SetAnswerAsync(request.SessionId, request.Sdp);
                
                if (success)
                {
                    _logger.LogInformation("✅ WebRTC Answer 已接收并处理: {SessionId}", request.SessionId);
                    return Ok(new ApiSuccessResponse<bool>
                    {
                        Success = true,
                        Data = true,
                        Message = "Answer 已接收"
                    });
                }
                else
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "会话不存在或已过期",
                        ErrorCode = ErrorCode.WebRtcSessionExpired
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理 WebRTC Answer 失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "处理 Answer 失败",
                    ErrorCode = ErrorCode.WebRtcAnswerProcessingFailed
                });
            }
        }
        
        /// <summary>
        /// 获取会话中待处理的 ICE Candidate（后端生成的新 candidate）
        /// </summary>
        [HttpGet("ice/{sessionId}")]
        public ActionResult<ResponseModel> GetPendingIceCandidates(string sessionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SessionId 不能为空",
                        ErrorCode = ErrorCode.SessionIdRequired
                    });
                }

                var candidates = _signalingService.GetPendingIceCandidates(sessionId);
                
                _logger.LogInformation("📤 获取待处理的 ICE Candidate: SessionId={SessionId}, Count={Count}",
                    sessionId, candidates.Count);
                
                if (candidates.Count > 0)
                {
                    // 显示完整的 candidate 字符串（至少显示到 ufrag 部分，如果有的话）
                    var candidateStrings = candidates.Select(c =>
                    {
                        if (string.IsNullOrWhiteSpace(c.candidate))
                        {
                            return "null";
                        }
                        var candidate = c.candidate;
                        // 如果包含 ufrag，显示到 ufrag 之后的部分
                        var ufragIndex = candidate.IndexOf("ufrag", StringComparison.OrdinalIgnoreCase);
                        if (ufragIndex >= 0)
                        {
                            var endIndex = Math.Min(ufragIndex + 30, candidate.Length);
                            return candidate.Substring(0, endIndex) + (endIndex < candidate.Length ? "..." : "");
                        }
                        // 否则显示前 100 个字符
                        return candidate.Length > 100 ? candidate.Substring(0, 100) + "..." : candidate;
                    });
                }

                var candidateList = candidates.Select(c => new
                {
                    candidate = c.candidate,
                    sdpMid = c.sdpMid,
                    sdpMLineIndex = c.sdpMLineIndex
                }).ToList();

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new { candidates = candidateList },
                    Message = $"获取到 {candidates.Count} 个待处理的 ICE Candidate"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 获取待处理的 ICE Candidate 失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取待处理的 ICE Candidate 失败: " + ex.Message,
                    ErrorCode = ErrorCode.WebRtcGetCandidatesFailed
                });
            }
        }

        /// <summary>
        /// 接收 ICE Candidate
        /// </summary>
        [HttpPost("ice")]
        public async Task<ActionResult<ResponseModel>> ReceiveIceCandidate([FromBody] WebRTCIceCandidateRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.SessionId))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SessionId 不能为空"
                    });
                }
                
                if (string.IsNullOrWhiteSpace(request.Candidate))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Candidate 不能为空",
                        ErrorCode = ErrorCode.CandidateRequired
                    });
                }
                
                _logger.LogInformation("📥 收到 ICE Candidate 请求: SessionId={SessionId}, Candidate={Candidate}, SdpMid={SdpMid}, SdpMLineIndex={SdpMLineIndex}",
                    request.SessionId, request.Candidate, request.SdpMid, request.SdpMLineIndex);
                
                var success = await _signalingService.AddIceCandidateAsync(
                    request.SessionId,
                    request.Candidate,
                    request.SdpMid ?? "",
                    request.SdpMLineIndex
                );
                
                if (success)
                {
                    var session = _signalingService.GetSession(request.SessionId);
                    _logger.LogInformation("✅ ICE Candidate 已接收并添加: SessionId={SessionId}, ConnectionState={ConnectionState}, IceConnectionState={IceConnectionState}",
                        request.SessionId,
                        session?.PeerConnection?.connectionState,
                        session?.PeerConnection?.iceConnectionState);
                    
                    return Ok(new ApiSuccessResponse<bool>
                    {
                        Success = true,
                        Data = true,
                        Message = "ICE Candidate 已接收"
                    });
                }
                else
                {
                    _logger.LogWarning("⚠️ ICE Candidate 添加失败: SessionId={SessionId}", request.SessionId);
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "会话不存在或已过期",
                        ErrorCode = ErrorCode.WebRtcSessionExpired
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理 ICE Candidate 失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "处理 ICE Candidate 失败",
                    ErrorCode = ErrorCode.WebRtcIceCandidateProcessingFailed
                });
            }
        }
        
        /// <summary>
        /// 主动请求关键帧
        /// </summary>
        [HttpPost("session/{sessionId}/keyframe")]
        public async Task<ActionResult<ResponseModel>> RequestKeyframe(string sessionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SessionId 不能为空",
                        ErrorCode = ErrorCode.SessionIdRequired
                    });
                }

                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "WebRTC 会话不存在",
                        ErrorCode = ErrorCode.WebRtcSessionNotFound
                    });
                }

                if (!session.StreamingSessionId.HasValue)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "会话尚未绑定流，无法请求关键帧",
                        ErrorCode = ErrorCode.WebRtcSessionNotBound
                    });
                }

                var stream = await _streamingService.GetStreamAsync(session.StreamingSessionId.Value);
                if (stream == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "远程播放流不存在或已结束",
                        ErrorCode = ErrorCode.StreamNotFound
                    });
                }

                await stream.RequestKeyframeAsync();
                _logger.LogInformation("🎯 已主动请求关键帧: SessionId={SessionId}, StreamingSessionId={StreamingSessionId}",
                    sessionId, session.StreamingSessionId);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "关键帧请求已发送"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 主动请求关键帧失败: {SessionId}", sessionId);
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "请求关键帧失败",
                    ErrorCode = ErrorCode.WebRtcKeyFrameRequestFailed
                });
            }
        }
        
        /// <summary>
        /// 将 WebRTC 会话连接到远程播放会话
        /// </summary>
        [HttpPost("connect/{webrtcSessionId}/{remotePlaySessionId}")]
        public async Task<ActionResult<ResponseModel>> ConnectToRemotePlaySession(
            string webrtcSessionId, 
            string remotePlaySessionId)
        {
            try
            {
                // 获取 WebRTC 接收器
                var receiver = _signalingService.GetReceiver(webrtcSessionId);
                if (receiver == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "WebRTC 会话不存在"
                    });
                }
                
                // 解析远程播放会话ID
                if (!Guid.TryParse(remotePlaySessionId, out var sessionGuid))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "无效的 RemotePlay Session ID"
                    });
                }
                
                // 获取流实例
                var stream = await _streamingService.GetStreamAsync(sessionGuid);
                if (stream == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "远程播放流不存在"
                    });
                }
                
                // 连接接收器到流
                stream.AddReceiver(receiver);
                
                // ✅ 设置 StreamingSessionId，以便关键帧请求功能可以正常工作
                var webrtcSession = _signalingService.GetSession(webrtcSessionId);
                if (webrtcSession != null)
                {
                    webrtcSession.StreamingSessionId = sessionGuid;
                    _logger.LogInformation("✅ 已设置 StreamingSessionId: {StreamingSessionId}", sessionGuid);
                }
                
                _logger.LogInformation("🔗 WebRTC 会话已连接到远程播放: WebRTC={WebRTC}, RemotePlay={RemotePlay}", 
                    webrtcSessionId, remotePlaySessionId);
                
                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "连接成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 连接会话失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "连接失败",
                    ErrorCode = ErrorCode.WebRtcConnectionFailed
                });
            }
        }
        
        /// <summary>
        /// 获取会话状态
        /// </summary>
        [HttpGet("session/{sessionId}")]
        public ActionResult<ResponseModel> GetSessionStatus(string sessionId)
        {
            try
            {
                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "会话不存在",
                        ErrorCode = ErrorCode.WebRtcSessionNotFound
                    });
                }
                
                var status = new WebRTCSessionStatus
                {
                    SessionId = session.SessionId,
                    ConnectionState = session.ConnectionState.ToString(),
                    IceConnectionState = session.IceConnectionState.ToString(),
                    CreatedAt = session.CreatedAt,
                    Age = DateTime.UtcNow - session.CreatedAt
                };
                
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = status,
                    Message = "获取会话状态成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 获取会话状态失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取会话状态失败",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }
        
        /// <summary>
        /// 获取所有会话
        /// </summary>
        [HttpGet("sessions")]
        public ActionResult<ResponseModel> GetAllSessions()
        {
            try
            {
                var sessions = _signalingService.GetAllSessions()
                    .Select(s => new WebRTCSessionStatus
                    {
                        SessionId = s.SessionId,
                        ConnectionState = s.ConnectionState.ToString(),
                        IceConnectionState = s.IceConnectionState.ToString(),
                        CreatedAt = s.CreatedAt,
                        Age = DateTime.UtcNow - s.CreatedAt
                    })
                    .ToList();
                
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = sessions,
                    Message = "获取会话列表成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 获取会话列表失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取会话列表失败",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }
        
        /// <summary>
        /// 删除会话
        /// </summary>
        [HttpDelete("session/{sessionId}")]
        public async Task<ActionResult<ResponseModel>> DeleteSession(string sessionId)
        {
            try
            {
                _signalingService.RemoveSession(sessionId);
                
                // 清理延时统计
                _latencyStats?.RemoveSession(sessionId);
                
                _logger.LogInformation("🗑️ WebRTC 会话已删除: {SessionId}", sessionId);
                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "会话已删除"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 删除会话失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "删除会话失败",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }
        
        /// <summary>
        /// 获取延时统计
        /// </summary>
        [HttpGet("latency/{sessionId}")]
        public ActionResult<ResponseModel> GetLatencyStats(string sessionId)
        {
            try
            {
                if (_latencyStats == null)
                {
                    return StatusCode(503, new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "延时统计服务未启用",
                        ErrorCode = ErrorCode.LatencyStatsServiceDisabled
                    });
                }
                
                var stats = _latencyStats.GetStats(sessionId);
                if (stats == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未找到该会话的延时统计",
                        ErrorCode = ErrorCode.LatencyStatsNotFound
                    });
                }
                
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = stats.GetSummary(),
                    Message = "获取延时统计成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 获取延时统计失败: {SessionId}", sessionId);
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取延时统计失败",
                    ErrorCode = ErrorCode.LatencyStatsGetFailed
                });
            }
        }
        
        /// <summary>
        /// 记录客户端接收时间
        /// </summary>
        [HttpPost("latency/receive")]
        public ActionResult<ResponseModel> RecordReceiveTime([FromBody] LatencyReceiveRequest request)
        {
            try
            {
                if (_latencyStats == null)
                {
                    return StatusCode(503, new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "延时统计服务未启用",
                        ErrorCode = ErrorCode.LatencyStatsServiceDisabled
                    });
                }
                
                _latencyStats.RecordPacketReceived(
                    request.SessionId,
                    request.PacketType,
                    request.FrameIndex,
                    request.ClientReceiveTime
                );
                
                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "记录接收时间成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 记录接收时间失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "记录接收时间失败",
                    ErrorCode = ErrorCode.LatencyStatsRecordFailed
                });
            }
        }
        
    }
    
    // DTO Models
    
}