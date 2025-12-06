using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Auth;
using RemotePlay.Models.Base;

namespace RemotePlay.Controllers
{
    /// <summary>
    /// 认证控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IAuthService authService,
            ILogger<AuthController> logger)
        {
            _authService = authService;
            _logger = logger;
        }

        /// <summary>
        /// 用户注册
        /// </summary>
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<ActionResult<ResponseModel>> Register([FromBody] RegisterRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "请求数据验证失败",
                        ErrorCode = ErrorCode.InvalidRequest
                    });
                }

                var response = await _authService.RegisterAsync(request);

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = response,
                    Message = "注册成功"
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户注册失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "服务器内部错误",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }

        /// <summary>
        /// 用户登录
        /// </summary>
        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<ActionResult<ResponseModel>> Login([FromBody] LoginRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "请求数据验证失败",
                        ErrorCode = ErrorCode.InvalidRequest
                    });
                }

                var response = await _authService.LoginAsync(request);

                if (response == null)
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "用户名或密码错误",
                        ErrorCode = ErrorCode.InvalidCredentials
                    });
                }

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = response,
                    Message = "登录成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户登录失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "服务器内部错误",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }

        /// <summary>
        /// 获取当前用户信息（用于测试）
        /// </summary>
        [HttpGet("me")]
        [Authorize]
        public async Task<ActionResult<ResponseModel>> GetCurrentUser()
        {
            try
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "无法获取用户信息",
                        ErrorCode = ErrorCode.Unauthorized
                    });
                }

                var user = await _authService.FindUserByIdAsync(userIdClaim);
                if (user == null)
                {
                    // 如果找不到用户，尝试从Claims获取信息
                    var username = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
                    var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

                    return Ok(new ApiSuccessResponse<object>
                    {
                        Success = true,
                        Data = new
                        {
                            Username = username,
                            Email = email,
                            UserId = userIdClaim
                        },
                        Message = "令牌有效"
                    });
                }

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new
                    {
                        UserId = user.Id,
                        Username = user.Username,
                        Email = user.Email,
                        LastLoginAt = user.LastLoginAt,
                        CreatedAt = user.CreatedAt
                    },
                    Message = "令牌有效"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户信息失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "服务器内部错误",
                    ErrorCode = ErrorCode.InternalServerError
                });
            }
        }
    }
}

