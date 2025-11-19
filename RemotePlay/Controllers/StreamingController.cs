using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
using RemotePlay.Models.Configuration;
using RemotePlay.Models.Context;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming;

namespace RemotePlay.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StreamingController : ControllerBase
    {
        private const string TurnConfigKey = "webrtc.turn_servers";

        private readonly IStreamingService _streamingService;
        private readonly IOptions<WebRTCConfig> _webRtcConfig;
        private readonly RPContext _context;
        private readonly ILogger<StreamingController> _logger;

        public StreamingController(
            IStreamingService streamingService,
            IOptions<WebRTCConfig> webRtcConfig,
            RPContext context,
            ILogger<StreamingController> logger)
        {
            _streamingService = streamingService;
            _webRtcConfig = webRtcConfig;
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// 主动请求关键帧（独立接口，不依赖 WebRTC 会话）
        /// </summary>
        [HttpPost("session/{sessionId:guid}/keyframe")]
        public async Task<ActionResult<ResponseModel>> RequestKeyframe(Guid sessionId)
        {
            try
            {
                if (sessionId == Guid.Empty)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "SessionId 不能为空"
                    });
                }

                var stream = await _streamingService.GetStreamAsync(sessionId);
                if (stream == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "远程播放流不存在或已结束"
                    });
                }

                await stream.RequestKeyframeAsync();

                _logger.LogInformation("🎯 StreamingController 请求关键帧成功: {SessionId}", sessionId);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "关键帧请求已发送"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ StreamingController 请求关键帧失败: {SessionId}", sessionId);
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "请求关键帧失败"
                });
            }
        }

        [HttpGet("session/{sessionId:guid}/health")]
        public async Task<ActionResult<ResponseModel>> GetStreamHealth(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "SessionId 不能为空"
                });
            }

            var stream = await _streamingService.GetStreamAsync(sessionId);
            if (stream == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "远程播放流不存在或已结束"
                });
            }

            var (snapshot, stats) = stream.GetStreamHealth();
            var dto = new StreamHealthDto
            {
                Timestamp = snapshot.Timestamp,
                Status = snapshot.LastStatus.ToString(),
                Message = snapshot.Message,
                ConsecutiveFailures = snapshot.ConsecutiveFailures,
                TotalRecoveredFrames = snapshot.TotalRecoveredFrames,
                TotalFrozenFrames = snapshot.TotalFrozenFrames,
                VideoReceived = stats.VideoReceived,
                VideoLost = stats.VideoLost,
                AudioReceived = stats.AudioReceived,
                AudioLost = stats.AudioLost
            };

            return Ok(new ApiSuccessResponse<StreamHealthDto>
            {
                Success = true,
                Data = dto,
                Message = "获取流健康状态成功"
            });
        }

        /// <summary>
        /// 获取当前用户的 WebRTC TURN 服务器配置
        /// </summary>
        [HttpGet("webrtc/turn-config")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> GetWebRTCTurnConfig(CancellationToken cancellationToken)
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未授权"
                    });
                }

                // 从配置文件中获取默认配置（通过环境变量配置）
                var defaultConfig = _webRtcConfig.Value ?? new WebRTCConfig();
                var result = new WebRTCConfig
                {
                    PublicIp = defaultConfig.PublicIp,
                    IcePortMin = defaultConfig.IcePortMin,
                    IcePortMax = defaultConfig.IcePortMax,
                    ShufflePorts = defaultConfig.ShufflePorts,
                    PreferLanCandidates = defaultConfig.PreferLanCandidates,
                    TurnServers = defaultConfig.TurnServers?.ToList() ?? new List<TurnServerConfig>()
                };

                // 尝试从数据库获取用户特定的 TURN 配置
                var userConfig = await _context.DeviceConfigs
                    .AsNoTracking()
                    .Where(dc => dc.UserId == userId
                        && dc.ConfigKey == TurnConfigKey
                        && dc.IsActive)
                    .OrderByDescending(dc => dc.UpdatedAt ?? dc.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (userConfig != null && !string.IsNullOrWhiteSpace(userConfig.ConfigValue))
                {
                    try
                    {
                        WebRTCConfig? userTurnConfig = null;

                        // 尝试从 ConfigJson 字段解析
                        if (userConfig.ConfigJson != null)
                        {
                            userTurnConfig = ParseTurnConfigFromJson(userConfig.ConfigJson);
                        }

                        // 如果 ConfigJson 没有结果，尝试从 ConfigValue 字段解析 JSON
                        if (userTurnConfig == null && !string.IsNullOrWhiteSpace(userConfig.ConfigValue))
                        {
                            var jsonObj = JObject.Parse(userConfig.ConfigValue);
                            userTurnConfig = ParseTurnConfigFromJson(jsonObj);
                        }

                        // 如果解析成功且有 TURN 服务器配置，则用用户配置覆盖默认配置
                        if (userTurnConfig != null && userTurnConfig.TurnServers.Count > 0)
                        {
                            result.TurnServers = userTurnConfig.TurnServers;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析用户 {UserId} 的 TURN 配置 JSON 失败，使用默认配置", userId);
                    }
                }

                return Ok(new ApiSuccessResponse<WebRTCConfig>
                {
                    Success = true,
                    Data = result,
                    Message = "获取 TURN 配置成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ StreamingController 获取 TURN 配置失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取 TURN 配置失败: " + ex.Message
                });
            }
        }

        private WebRTCConfig? ParseTurnConfigFromJson(JObject json)
        {
            try
            {
                var turnServers = new List<TurnServerConfig>();

                // 支持多种格式：
                // 1. { "turnServers": [...] }
                // 2. { "servers": [...] }
                // 3. { "TurnServers": [...] } (直接序列化的 WebRTCConfig)
                var serversToken = json["turnServers"] ?? json["TurnServers"] ?? json["servers"];
                if (serversToken == null || serversToken.Type != JTokenType.Array)
                {
                    return null;
                }

                foreach (var serverToken in serversToken)
                {
                    if (serverToken.Type != JTokenType.Object)
                    {
                        continue;
                    }

                    var serverObj = (JObject)serverToken;
                    // 支持 "url" 和 "urls" 两种字段名
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

                return new WebRTCConfig
                {
                    TurnServers = turnServers
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 解析 TURN 配置 JSON 对象失败");
                return null;
            }
        }
    }

    public class StreamHealthDto
    {
        public DateTime Timestamp { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int TotalRecoveredFrames { get; set; }
        public int TotalFrozenFrames { get; set; }
        public int VideoReceived { get; set; }
        public int VideoLost { get; set; }
        public int AudioReceived { get; set; }
        public int AudioLost { get; set; }
    }
}

