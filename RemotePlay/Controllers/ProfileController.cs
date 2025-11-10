using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
using RemotePlay.Models.Profile;

namespace RemotePlay.Controllers
{
    /// <summary>
    /// Controller for managing PSN user profiles
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ProfileController : ControllerBase
    {
        private readonly IProfileService _profileService;
        private readonly ILogger<ProfileController> _logger;

        public ProfileController(IProfileService profileService, ILogger<ProfileController> logger)
        {
            _profileService = profileService;
            _logger = logger;
        }

        /// <summary>
        /// Get PSN login URL for user authentication
        /// </summary>
        /// <param name="redirectUri">Optional custom redirect URI for OAuth callback</param>
        /// <returns>Login URL</returns>
        [HttpGet("login-url")]
        public ActionResult<ResponseModel> GetLoginUrl([FromQuery] string? redirectUri = null)
        {
            try
            {
                var url = _profileService.GetLoginUrl(redirectUri);
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new { loginUrl = url },
                    Message = "Login URL generated successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating login URL");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Load profiles from file
        /// </summary>
        /// <param name="path">Optional path to profiles file</param>
        /// <returns>Profiles collection</returns>
        [HttpGet("load")]
        public async Task<ActionResult<ResponseModel>> LoadProfiles([FromQuery] string? path = null)
        {
            try
            {
                var profiles = await _profileService.LoadAsync(path ?? string.Empty);
                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new
                    {
                        usernames = profiles.Usernames,
                        users = profiles.Users.Select(u => new
                        {
                            name = u.Name,
                            id = u.Id,
                            hosts = u.Hosts.Select(h => new
                            {
                                name = h.Name,
                                type = h.Type,
                                registKey = h.RegistKey,
                                rpKey = h.RpKey
                            })
                        })
                    },
                    Message = "Profiles loaded successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading profiles");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Create new PSN user from OAuth redirect URL
        /// </summary>
        /// <param name="request">Request containing redirect URL</param>
        /// <returns>Created user profile</returns>
        [HttpPost("new-user")]
        public async Task<ActionResult<ResponseModel>> NewUser([FromBody] NewUserRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.RedirectUrl))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Redirect URL is required"
                    });
                }

                var profiles = await _profileService.LoadAsync(request.ProfilePath ?? string.Empty);
                var userProfile = await _profileService.NewUserAsync(profiles, request.RedirectUrl, request.Save);

                if (userProfile == null)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Failed to create user profile"
                    });
                }

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new
                    {
                        name = userProfile.Name,
                        id = userProfile.Id,
                        credentials = userProfile.Credentials,  // Add credentials for device registration
                        hosts = userProfile.Hosts.Select(h => new
                        {
                            name = h.Name,
                            type = h.Type
                        })
                    },
                    Message = "User profile created successfully. Use 'credentials' for device registration."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new user");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Get specific user profile
        /// </summary>
        /// <param name="username">PSN username</param>
        /// <param name="path">Optional path to profiles file</param>
        /// <returns>User profile</returns>
        [HttpGet("user/{username}")]
        public async Task<ActionResult<ResponseModel>> GetUserProfile(string username, [FromQuery] string? path = null)
        {
            try
            {
                if (string.IsNullOrEmpty(username))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Username is required"
                    });
                }

                var profiles = await _profileService.LoadAsync(path ?? string.Empty);
                var userProfile = profiles.GetUserProfile(username);

                if (userProfile == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = $"User '{username}' not found"
                    });
                }

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new
                    {
                        name = userProfile.Name,
                        id = userProfile.Id,
                        credentials = userProfile.Credentials,  // Include credentials
                        hosts = userProfile.Hosts.Select(h => new
                        {
                            name = h.Name,
                            type = h.Type,
                            registKey = h.RegistKey,
                            rpKey = h.RpKey
                        })
                    },
                    Message = "User profile retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user profile");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Get all users registered with a specific device
        /// </summary>
        /// <param name="deviceId">Device ID / Mac Address</param>
        /// <param name="path">Optional path to profiles file</param>
        /// <returns>List of usernames</returns>
        [HttpGet("device/{deviceId}/users")]
        public async Task<ActionResult<ResponseModel>> GetDeviceUsers(string deviceId, [FromQuery] string? path = null)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Device ID is required"
                    });
                }

                var profiles = await _profileService.LoadAsync(path ?? string.Empty);
                var users = profiles.GetUsers(deviceId);

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new { deviceId, users },
                    Message = "Device users retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting device users");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Remove a user profile
        /// </summary>
        /// <param name="username">Username to remove</param>
        /// <param name="path">Optional path to profiles file</param>
        /// <returns>Success response</returns>
        [HttpDelete("user/{username}")]
        public async Task<ActionResult<ResponseModel>> RemoveUser(string username, [FromQuery] string? path = null)
        {
            try
            {
                if (string.IsNullOrEmpty(username))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Username is required"
                    });
                }

                var profiles = await _profileService.LoadAsync(path ?? string.Empty);
                profiles.RemoveUser(username);
                await _profileService.SaveAsync(profiles, path ?? string.Empty);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = $"User '{username}' removed successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing user");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// OAuth callback endpoint - receives PSN OAuth redirect and forwards to frontend
        /// </summary>
        /// <param name="code">Authorization code from PSN</param>
        /// <param name="state">State parameter</param>
        /// <param name="error">Error if any</param>
        /// <param name="errorDescription">Error description if any</param>
        /// <returns>Redirect to frontend callback page</returns>
        [HttpGet("oauth-callback")]
        [AllowAnonymous]
        public ActionResult OAuthCallback(
            [FromQuery] string? code = null,
            [FromQuery] string? state = null,
            [FromQuery] string? error = null,
            [FromQuery] string? errorDescription = null)
        {
            try
            {
                // 构建前端回调URL
                // 优先从Referer获取，如果没有则从Origin获取
                var referer = Request.Headers["Referer"].ToString();
                var origin = Request.Headers["Origin"].ToString();
                
                string frontendUrl;
                if (!string.IsNullOrEmpty(referer))
                {
                    // 从Referer提取前端URL（去掉路径部分）
                    try
                    {
                        var refererUri = new Uri(referer);
                        frontendUrl = $"{refererUri.Scheme}://{refererUri.Host}";
                        if (refererUri.Port != 80 && refererUri.Port != 443 && refererUri.Port > 0)
                        {
                            frontendUrl += $":{refererUri.Port}";
                        }
                    }
                    catch
                    {
                        frontendUrl = origin;
                    }
                }
                else if (!string.IsNullOrEmpty(origin))
                {
                    frontendUrl = origin;
                }
                else
                {
                    // 从配置或环境变量获取前端URL
                    var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                    frontendUrl = config["Frontend:BaseUrl"] ?? "http://localhost:5173";
                }

                // 构建查询参数
                var queryParams = new List<string>();
                if (!string.IsNullOrEmpty(code))
                    queryParams.Add($"code={Uri.EscapeDataString(code)}");
                if (!string.IsNullOrEmpty(state))
                    queryParams.Add($"state={Uri.EscapeDataString(state)}");
                if (!string.IsNullOrEmpty(error))
                    queryParams.Add($"error={Uri.EscapeDataString(error)}");
                if (!string.IsNullOrEmpty(errorDescription))
                    queryParams.Add($"error_description={Uri.EscapeDataString(errorDescription)}");

                var queryString = string.Join("&", queryParams);
                var redirectUrl = $"{frontendUrl.TrimEnd('/')}/oauth/callback?{queryString}";

                _logger.LogInformation("OAuth callback received, redirecting to frontend: {RedirectUrl}", redirectUrl);
                
                return Redirect(redirectUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling OAuth callback");
                // 即使出错也重定向到前端，让前端显示错误
                var frontendUrl = Request.Headers["Origin"].ToString() ?? "http://localhost:5173";
                return Redirect($"{frontendUrl.TrimEnd('/')}/oauth/callback?error=server_error&error_description={Uri.EscapeDataString(ex.Message)}");
            }
        }

        /// <summary>
        /// Set default profiles file path
        /// </summary>
        /// <param name="request">Request containing path</param>
        /// <returns>Success response</returns>
        [HttpPost("set-default-path")]
        public ActionResult<ResponseModel> SetDefaultPath([FromBody] SetPathRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Path))
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "Path is required"
                    });
                }

                Profiles.SetDefaultPath(request.Path);

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new { defaultPath = request.Path },
                    Message = "Default path set successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting default path");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }
    }

    /// <summary>
    /// Request model for creating new user
    /// </summary>
    public class NewUserRequest
    {
        /// <summary>
        /// OAuth redirect URL
        /// </summary>
        public string RedirectUrl { get; set; } = string.Empty;

        /// <summary>
        /// Path to profiles file
        /// </summary>
        public string? ProfilePath { get; set; }

        /// <summary>
        /// Whether to save after creating user
        /// </summary>
        public bool Save { get; set; } = true;
    }

    /// <summary>
    /// Request model for setting default path
    /// </summary>
    public class SetPathRequest
    {
        /// <summary>
        /// File path
        /// </summary>
        public string Path { get; set; } = string.Empty;
    }
}

