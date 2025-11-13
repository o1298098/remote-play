using Microsoft.AspNetCore.Mvc;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming;

namespace RemotePlay.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StreamingController : ControllerBase
    {
        private readonly IStreamingService _streamingService;
        private readonly ILogger<StreamingController> _logger;

        public StreamingController(
            IStreamingService streamingService,
            ILogger<StreamingController> logger)
        {
            _streamingService = streamingService;
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

