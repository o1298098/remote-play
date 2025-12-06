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
        private const string WebRTCConfigKey = "webrtc.config";
        private const string SettingsCategory = "webrtc";

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
                        ErrorMessage = "SessionId 不能为空",
                        ErrorCode = ErrorCode.SessionIdRequired
                    });
                }

                var stream = await _streamingService.GetStreamAsync(sessionId);
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
                    ErrorMessage = "请求关键帧失败",
                    ErrorCode = ErrorCode.KeyFrameRequestFailed
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
                    ErrorMessage = "SessionId 不能为空",
                    ErrorCode = ErrorCode.SessionIdRequired
                });
            }

            var stream = await _streamingService.GetStreamAsync(sessionId);
            if (stream == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "远程播放流不存在或已结束",
                    ErrorCode = ErrorCode.StreamNotFound
                });
            }

            var (snapshot, stats) = stream.GetStreamHealth();
            var dto = new StreamHealthDto
            {
                Timestamp = snapshot.Timestamp,
                Status = snapshot.LastStatus.ToString(),
                Message = snapshot.Message,
                ConsecutiveFailures = snapshot.ConsecutiveFailures,
                
                // 帧统计
                TotalRecoveredFrames = snapshot.TotalRecoveredFrames,
                TotalFrozenFrames = snapshot.TotalFrozenFrames,
                TotalDroppedFrames = snapshot.TotalDroppedFrames,
                DeltaRecoveredFrames = snapshot.DeltaRecoveredFrames,
                DeltaFrozenFrames = snapshot.DeltaFrozenFrames,
                DeltaDroppedFrames = snapshot.DeltaDroppedFrames,
                
                // 最近窗口统计
                RecentWindowSeconds = snapshot.RecentWindowSeconds,
                RecentSuccessFrames = snapshot.RecentSuccessFrames,
                RecentRecoveredFrames = snapshot.RecentRecoveredFrames,
                RecentFrozenFrames = snapshot.RecentFrozenFrames,
                RecentDroppedFrames = snapshot.RecentDroppedFrames,
                RecentFps = snapshot.RecentFps,
                AverageFrameIntervalMs = snapshot.AverageFrameIntervalMs,
                LastFrameTimestampUtc = snapshot.LastFrameTimestampUtc,
                
                // 流统计和码率
                TotalFrames = snapshot.TotalFrames,
                TotalBytes = snapshot.TotalBytes,
                MeasuredBitrateMbps = snapshot.MeasuredBitrateMbps,
                FramesLost = snapshot.FramesLost,
                FrameIndexPrev = snapshot.FrameIndexPrev,
                
                // 包统计
                VideoReceived = stats.VideoReceived,
                VideoLost = stats.VideoLost,
                VideoTimeoutDropped = stats.VideoTimeoutDropped,
                AudioReceived = stats.AudioReceived,
                AudioLost = stats.AudioLost,
                AudioTimeoutDropped = stats.AudioTimeoutDropped,
                PendingPackets = stats.PendingPackets,
                
                // IDR 请求统计
                TotalIdrRequests = stats.TotalIdrRequests,
                IdrRequestsRecent = stats.IdrRequestsRecent,
                IdrRequestWindowSeconds = stats.IdrRequestWindowSeconds,
                LastIdrRequestUtc = stats.LastIdrRequestUtc,
                
                // FEC 统计
                FecAttempts = stats.FecAttempts,
                FecSuccess = stats.FecSuccess,
                FecFailures = stats.FecFailures,
                FecSuccessRate = stats.FecSuccessRate,
                
                // 输出统计
                FrameOutputFps = stats.FrameOutputFps,
                FrameIntervalMs = stats.FrameIntervalMs
            };

            return Ok(new ApiSuccessResponse<StreamHealthDto>
            {
                Success = true,
                Data = dto,
                Message = "获取流健康状态成功"
            });
        }

        /// <summary>
        /// 获取 WebRTC TURN 服务器配置（从 Settings 表读取）
        /// </summary>
        [HttpGet("webrtc/turn-config")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> GetWebRTCTurnConfig(CancellationToken cancellationToken)
        {
            try
            {
                // 从 Settings 表读取 TURN 配置
                var setting = await _context.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == TurnConfigKey)
                    .FirstOrDefaultAsync(cancellationToken);

                var result = new WebRTCConfig
                {
                    TurnServers = new List<TurnServerConfig>()
                };

                if (setting != null)
                {
                    try
                    {
                        // 优先从 ValueJson 字段读取
                        if (setting.ValueJson != null)
                        {
                            var turnConfig = ParseTurnConfigFromJson(setting.ValueJson);
                            if (turnConfig != null && turnConfig.TurnServers.Count > 0)
                            {
                                result.TurnServers = turnConfig.TurnServers;
                            }
                            // 解析 forceUseTurn
                            var forceUseTurnToken = setting.ValueJson["forceUseTurn"] ?? setting.ValueJson["ForceUseTurn"];
                            if (forceUseTurnToken != null && forceUseTurnToken.Type == JTokenType.Boolean)
                            {
                                result.ForceUseTurn = forceUseTurnToken.Value<bool>();
                            }
                        }
                        // 如果 ValueJson 为空，尝试从 Value 字段解析 JSON
                        else if (!string.IsNullOrWhiteSpace(setting.Value))
                        {
                            var jsonObj = JObject.Parse(setting.Value);
                            var turnConfig = ParseTurnConfigFromJson(jsonObj);
                            if (turnConfig != null && turnConfig.TurnServers.Count > 0)
                            {
                                result.TurnServers = turnConfig.TurnServers;
                            }
                            // 解析 forceUseTurn
                            var forceUseTurnToken = jsonObj["forceUseTurn"] ?? jsonObj["ForceUseTurn"];
                            if (forceUseTurnToken != null && forceUseTurnToken.Type == JTokenType.Boolean)
                            {
                                result.ForceUseTurn = forceUseTurnToken.Value<bool>();
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析 TURN 配置 JSON 失败，使用空配置");
                    }
                }

                // 如果 TURN 配置中没有 forceUseTurn，尝试从完整的 WebRTC 配置中读取
                // 注意：如果 result.ForceUseTurn 是默认值 false，我们也需要检查 WebRTC 配置
                // 因为 false 可能是默认值，也可能是用户明确设置的 false
                // 我们通过检查是否在 TURN 配置中找到了 forceUseTurn 字段来判断
                bool foundForceUseTurnInTurnConfig = false;
                if (setting != null)
                {
                    try
                    {
                        if (setting.ValueJson != null)
                        {
                            foundForceUseTurnInTurnConfig = setting.ValueJson["forceUseTurn"] != null || setting.ValueJson["ForceUseTurn"] != null;
                        }
                        else if (!string.IsNullOrWhiteSpace(setting.Value))
                        {
                            var jsonObj = JObject.Parse(setting.Value);
                            foundForceUseTurnInTurnConfig = jsonObj["forceUseTurn"] != null || jsonObj["ForceUseTurn"] != null;
                        }
                    }
                    catch
                    {
                        // 忽略解析错误
                    }
                }

                if (!foundForceUseTurnInTurnConfig)
                {
                    var webrtcSetting = await _context.Settings
                        .AsNoTracking()
                        .Where(s => s.Key == WebRTCConfigKey)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (webrtcSetting != null)
                    {
                        try
                        {
                            JObject? jsonObj = webrtcSetting.ValueJson ?? 
                                (!string.IsNullOrWhiteSpace(webrtcSetting.Value) ? JObject.Parse(webrtcSetting.Value) : null);
                            
                            if (jsonObj != null)
                            {
                                var forceUseTurnToken = jsonObj["forceUseTurn"] ?? jsonObj["ForceUseTurn"];
                                if (forceUseTurnToken != null && forceUseTurnToken.Type == JTokenType.Boolean)
                                {
                                    result.ForceUseTurn = forceUseTurnToken.Value<bool>();
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "⚠️ 从 WebRTC 配置读取 forceUseTurn 失败");
                        }
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
                    ErrorMessage = "获取 TURN 配置失败: " + ex.Message,
                    ErrorCode = ErrorCode.TurnConfigGetFailed
                });
            }
        }

        /// <summary>
        /// 保存 WebRTC TURN 服务器配置到 Settings 表
        /// </summary>
        [HttpPost("webrtc/turn-config")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> SaveWebRTCTurnConfig(
            [FromBody] WebRTCConfig config,
            CancellationToken cancellationToken)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "配置不能为空"
                    });
                }

                // 构建 JSON 对象
                var jsonObj = new JObject
                {
                    ["turnServers"] = new JArray(
                        (config.TurnServers ?? new List<TurnServerConfig>())
                            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                            .Select(s => new JObject
                            {
                                ["url"] = s.Url,
                                ["username"] = s.Username ?? string.Empty,
                                ["credential"] = s.Credential ?? string.Empty
                            })
                    )
                };

                // 查找或创建 Settings 记录
                var setting = await _context.Settings
                    .Where(s => s.Key == TurnConfigKey)
                    .FirstOrDefaultAsync(cancellationToken);

                if (setting == null)
                {
                    // 创建新记录
                    setting = new Models.DB.Base.Settings
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Key = TurnConfigKey,
                        ValueJson = jsonObj,
                        Category = SettingsCategory,
                        Description = "WebRTC TURN 服务器配置",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Settings.Add(setting);
                }
                else
                {
                    // 更新现有记录
                    setting.ValueJson = jsonObj;
                    setting.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("✅ TURN 配置已保存到 Settings 表: {Count} 个服务器", 
                    config.TurnServers?.Count ?? 0);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "保存 TURN 配置成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ StreamingController 保存 TURN 配置失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "保存 TURN 配置失败: " + ex.Message,
                    ErrorCode = ErrorCode.TurnConfigSaveFailed
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

        /// <summary>
        /// 获取完整的 WebRTC 配置（包括 PublicIp, IcePortMin, IcePortMax, TurnServers）
        /// </summary>
        [HttpGet("webrtc/config")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> GetWebRTCConfig(CancellationToken cancellationToken)
        {
            try
            {
                // 从 Settings 表读取 WebRTC 配置
                var setting = await _context.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == WebRTCConfigKey)
                    .FirstOrDefaultAsync(cancellationToken);

                var result = new WebRTCConfig
                {
                    PublicIp = _webRtcConfig.Value.PublicIp,
                    IcePortMin = _webRtcConfig.Value.IcePortMin,
                    IcePortMax = _webRtcConfig.Value.IcePortMax,
                    TurnServers = new List<TurnServerConfig>()
                };

                if (setting != null)
                {
                    try
                    {
                        // 优先从 ValueJson 字段读取
                        if (setting.ValueJson != null)
                        {
                            var config = ParseWebRTCConfigFromJson(setting.ValueJson);
                            if (config != null)
                            {
                                // 如果 JSON 中明确设置了值（包括 null），则使用该值
                                if (setting.ValueJson["publicIp"] != null || setting.ValueJson["PublicIp"] != null)
                                    result.PublicIp = config.PublicIp;
                                if (setting.ValueJson["icePortMin"] != null || setting.ValueJson["IcePortMin"] != null)
                                    result.IcePortMin = config.IcePortMin;
                                if (setting.ValueJson["icePortMax"] != null || setting.ValueJson["IcePortMax"] != null)
                                    result.IcePortMax = config.IcePortMax;
                                if (config.TurnServers != null && config.TurnServers.Count > 0)
                                    result.TurnServers = config.TurnServers;
                                // 复制 ForceUseTurn
                                if (setting.ValueJson["forceUseTurn"] != null || setting.ValueJson["ForceUseTurn"] != null)
                                    result.ForceUseTurn = config.ForceUseTurn;
                            }
                        }
                        // 如果 ValueJson 为空，尝试从 Value 字段解析 JSON
                        else if (!string.IsNullOrWhiteSpace(setting.Value))
                        {
                            var jsonObj = JObject.Parse(setting.Value);
                            var config = ParseWebRTCConfigFromJson(jsonObj);
                            if (config != null)
                            {
                                // 如果 JSON 中明确设置了值（包括 null），则使用该值
                                if (jsonObj["publicIp"] != null || jsonObj["PublicIp"] != null)
                                    result.PublicIp = config.PublicIp;
                                if (jsonObj["icePortMin"] != null || jsonObj["IcePortMin"] != null)
                                    result.IcePortMin = config.IcePortMin;
                                if (jsonObj["icePortMax"] != null || jsonObj["IcePortMax"] != null)
                                    result.IcePortMax = config.IcePortMax;
                                if (config.TurnServers != null && config.TurnServers.Count > 0)
                                    result.TurnServers = config.TurnServers;
                                // 复制 ForceUseTurn
                                if (jsonObj["forceUseTurn"] != null || jsonObj["ForceUseTurn"] != null)
                                    result.ForceUseTurn = config.ForceUseTurn;
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析 WebRTC 配置 JSON 失败，使用默认配置");
                    }
                }

                // 同时读取 TURN 配置（如果存在单独的 TURN 配置，优先使用）
                var turnSetting = await _context.Settings
                    .AsNoTracking()
                    .Where(s => s.Key == TurnConfigKey)
                    .FirstOrDefaultAsync(cancellationToken);

                if (turnSetting != null)
                {
                    try
                    {
                        var turnConfig = turnSetting.ValueJson != null
                            ? ParseTurnConfigFromJson(turnSetting.ValueJson)
                            : !string.IsNullOrWhiteSpace(turnSetting.Value)
                                ? ParseTurnConfigFromJson(JObject.Parse(turnSetting.Value))
                                : null;

                        if (turnConfig != null && turnConfig.TurnServers.Count > 0)
                        {
                            result.TurnServers = turnConfig.TurnServers;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "⚠️ 解析 TURN 配置 JSON 失败");
                    }
                }

                return Ok(new ApiSuccessResponse<WebRTCConfig>
                {
                    Success = true,
                    Data = result,
                    Message = "获取 WebRTC 配置成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ StreamingController 获取 WebRTC 配置失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取 WebRTC 配置失败: " + ex.Message,
                    ErrorCode = ErrorCode.WebRtcConfigGetFailed
                });
            }
        }

        /// <summary>
        /// 保存完整的 WebRTC 配置到 Settings 表
        /// </summary>
        [HttpPost("webrtc/config")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> SaveWebRTCConfig(
            [FromBody] WebRTCConfig config,
            CancellationToken cancellationToken)
        {
            try
            {
                if (config == null)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "配置不能为空",
                        ErrorCode = ErrorCode.ConfigRequired
                    });
                }

                // 先读取现有配置，以便合并更新（保留未修改的字段）
                var existingSetting = await _context.Settings
                    .Where(s => s.Key == WebRTCConfigKey)
                    .FirstOrDefaultAsync(cancellationToken);
                
                var jsonObj = existingSetting?.ValueJson != null 
                    ? JObject.FromObject(existingSetting.ValueJson) 
                    : new JObject();
                
                // 总是更新 PublicIp（包括 null/空值，以支持清除）
                // 如果传入 null 或空字符串，则设置为 null 以清除该字段
                if (string.IsNullOrWhiteSpace(config.PublicIp))
                    jsonObj["publicIp"] = JValue.CreateNull();
                else
                    jsonObj["publicIp"] = config.PublicIp.Trim();
                
                // 总是更新端口范围（包括 null 值，以支持清除）
                if (config.IcePortMin.HasValue)
                    jsonObj["icePortMin"] = config.IcePortMin.Value;
                else
                    jsonObj["icePortMin"] = JValue.CreateNull();
                
                if (config.IcePortMax.HasValue)
                    jsonObj["icePortMax"] = config.IcePortMax.Value;
                else
                    jsonObj["icePortMax"] = JValue.CreateNull();

                // 总是更新 TURN 服务器配置（包括空数组，以支持清除所有服务器）
                // 如果 config.TurnServers 为 null，则不更新该字段（保持原值）
                if (config.TurnServers != null)
                {
                    var validServers = config.TurnServers
                        .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                        .Select(s => new JObject
                        {
                            ["url"] = s.Url,
                            ["username"] = s.Username ?? string.Empty,
                            ["credential"] = s.Credential ?? string.Empty
                        })
                        .ToList();
                    
                    jsonObj["turnServers"] = new JArray(validServers);
                }

                // 总是更新 ForceUseTurn
                jsonObj["forceUseTurn"] = config.ForceUseTurn;

                // 使用之前查询的 existingSetting，避免重复查询
                var setting = existingSetting;

                if (setting == null)
                {
                    // 创建新记录
                    setting = new Models.DB.Base.Settings
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Key = WebRTCConfigKey,
                        ValueJson = jsonObj,
                        Category = SettingsCategory,
                        Description = "WebRTC 完整配置（PublicIp, IcePortMin, IcePortMax, TurnServers）",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Settings.Add(setting);
                }
                else
                {
                    // 更新现有记录
                    setting.ValueJson = jsonObj;
                    setting.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("✅ WebRTC 配置已保存到 Settings 表: PublicIp={PublicIp}, IcePortMin={IcePortMin}, IcePortMax={IcePortMax}, TurnServers={Count}",
                    config.PublicIp, config.IcePortMin, config.IcePortMax, config.TurnServers?.Count ?? 0);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "保存 WebRTC 配置成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ StreamingController 保存 WebRTC 配置失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "保存 WebRTC 配置失败: " + ex.Message,
                    ErrorCode = ErrorCode.WebRtcConfigSaveFailed
                });
            }
        }

        private WebRTCConfig? ParseWebRTCConfigFromJson(JObject json)
        {
            try
            {
                var config = new WebRTCConfig();

                // 解析 PublicIp（支持 null）
                var publicIpToken = json["publicIp"] ?? json["PublicIp"];
                if (publicIpToken != null)
                {
                    if (publicIpToken.Type == JTokenType.Null)
                        config.PublicIp = null;
                    else
                        config.PublicIp = publicIpToken.ToString();
                }

                // 解析 IcePortMin（支持 null）
                var icePortMinToken = json["icePortMin"] ?? json["IcePortMin"];
                if (icePortMinToken != null)
                {
                    if (icePortMinToken.Type == JTokenType.Null)
                        config.IcePortMin = null;
                    else if (icePortMinToken.Type == JTokenType.Integer)
                        config.IcePortMin = icePortMinToken.Value<int>();
                }

                // 解析 IcePortMax（支持 null）
                var icePortMaxToken = json["icePortMax"] ?? json["IcePortMax"];
                if (icePortMaxToken != null)
                {
                    if (icePortMaxToken.Type == JTokenType.Null)
                        config.IcePortMax = null;
                    else if (icePortMaxToken.Type == JTokenType.Integer)
                        config.IcePortMax = icePortMaxToken.Value<int>();
                }

                // 解析 TurnServers
                var turnServers = new List<TurnServerConfig>();
                var serversToken = json["turnServers"] ?? json["TurnServers"] ?? json["servers"];
                if (serversToken != null && serversToken.Type == JTokenType.Array)
                {
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
                }
                config.TurnServers = turnServers;

                // 解析 ForceUseTurn
                var forceUseTurnToken = json["forceUseTurn"] ?? json["ForceUseTurn"];
                if (forceUseTurnToken != null && forceUseTurnToken.Type == JTokenType.Boolean)
                {
                    config.ForceUseTurn = forceUseTurnToken.Value<bool>();
                }

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 解析 WebRTC 配置 JSON 对象失败");
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
        
        // 帧统计
        public int TotalRecoveredFrames { get; set; }
        public int TotalFrozenFrames { get; set; }
        public int TotalDroppedFrames { get; set; }
        public int DeltaRecoveredFrames { get; set; }
        public int DeltaFrozenFrames { get; set; }
        public int DeltaDroppedFrames { get; set; }
        
        // 最近窗口统计
        public int RecentWindowSeconds { get; set; }
        public int RecentSuccessFrames { get; set; }
        public int RecentRecoveredFrames { get; set; }
        public int RecentFrozenFrames { get; set; }
        public int RecentDroppedFrames { get; set; }
        public double RecentFps { get; set; }
        public double AverageFrameIntervalMs { get; set; }
        public DateTime LastFrameTimestampUtc { get; set; }
        
        // 流统计和码率
        public ulong TotalFrames { get; set; }
        public ulong TotalBytes { get; set; }
        public double MeasuredBitrateMbps { get; set; }
        public int FramesLost { get; set; }
        public int FrameIndexPrev { get; set; }
        
        // 包统计
        public int VideoReceived { get; set; }
        public int VideoLost { get; set; }
        public int VideoTimeoutDropped { get; set; }
        public int AudioReceived { get; set; }
        public int AudioLost { get; set; }
        public int AudioTimeoutDropped { get; set; }
        public int PendingPackets { get; set; }
        
        // IDR 请求统计
        public int TotalIdrRequests { get; set; }
        public int IdrRequestsRecent { get; set; }
        public int IdrRequestWindowSeconds { get; set; }
        public DateTime? LastIdrRequestUtc { get; set; }
        
        // FEC 统计
        public int FecAttempts { get; set; }
        public int FecSuccess { get; set; }
        public int FecFailures { get; set; }
        public double FecSuccessRate { get; set; }
        
        // 输出统计
        public double FrameOutputFps { get; set; }
        public double FrameIntervalMs { get; set; }
    }
}

