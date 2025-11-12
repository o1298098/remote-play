using Microsoft.AspNetCore.Mvc;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
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
    }
}

