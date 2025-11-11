
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Base;
using RemotePlay.Models.Context;
using RemotePlay.Models.PlayStation;
using RemotePlay.Services;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Utils;
using System;
using System.Linq;
using System.Security.Claims;

namespace RemotePlay.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PlayStationController : ControllerBase
    {
        private readonly IRemotePlayService _remotePlayService;
        private readonly ILogger<PlayStationController> _logger;

        private readonly ISessionService _sessionService;
        private readonly IStreamingService _streamingService;
        private readonly IControllerService _controllerService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IRegisterService _reg;
        private readonly RPContext _rpContext;
        private readonly IWebHostEnvironment _env;
        private readonly IDeviceSettingsService _deviceSettingsService;
        private readonly IdGenerator _idGenerator;

        public PlayStationController(
            IRegisterService registeredServices,
            IRemotePlayService remotePlayService,
            ISessionService sessionService,
            IStreamingService streamingService,
            IControllerService controllerService,
            RPContext rpContext,
            ILogger<PlayStationController> logger,
            ILoggerFactory loggerFactory,
            IWebHostEnvironment env,
            IDeviceSettingsService deviceSettingsService)
        {
            _remotePlayService = remotePlayService;
            _reg = registeredServices;
            _rpContext = rpContext;
            _sessionService = sessionService;
            _streamingService = streamingService;
            _controllerService = controllerService;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _idGenerator = new IdGenerator(0,0);
            _env = env;
            _deviceSettingsService = deviceSettingsService;
        }

        /// <summary>
        /// 发现本地网络中的PlayStation主机
        /// </summary>
        /// <param name="timeoutMs">超时时间（毫秒），默认2000ms</param>
        /// <returns>发现的主机列表</returns>
        [HttpGet("discover")]
        public async Task<ActionResult> DiscoverConsoles(int? timeoutMs = null)
        {
            try
            {
                _logger.LogInformation("开始设备发现，超时时间: {TimeoutMs}ms", timeoutMs ?? 2000);

                var consoles = await _remotePlayService.DiscoverDevicesAsync(timeoutMs);

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = consoles,
                    Message = $"发现 {consoles.Count} 个设备"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备发现失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备发现失败: " + ex.Message
                });
            }
        }

        /// <summary>
        /// 发现特定IP的PlayStation主机
        /// </summary>
        /// <param name="hostIp">主机IP地址</param>
        /// <param name="timeoutMs">超时时间（毫秒），默认2000ms</param>
        /// <returns>主机信息</returns>
        [HttpGet("discover/{hostIp}")]
        public async Task<ActionResult> DiscoverConsole(string hostIp, int? timeoutMs = null)
        {
            var console = await _remotePlayService.DiscoverDeviceAsync(hostIp, timeoutMs);

            if (console == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = $"未找到主机: {hostIp}"
                });
            }

            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = console,
                Message = "设备发现成功"
            });
        }

        /// <summary>
        /// wake up console
        /// </summary>
        /// <param name="hostId">console host_id</param>
        /// <returns>status</returns>
        [HttpPost("wakeup")]
        public async Task<ActionResult> WakeUpConsole(string hostId)
        {
            var _device = await _rpContext.PSDevices
                .AsNoTracking()
                .Where(x => x.HostId == hostId && x.IsRegistered == true)
                .FirstOrDefaultAsync();
            if (_device == null)
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备未找到"
                });
            if (_device.IpAddress == null)
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "ip address is empty"
                });
            if (_device.RegistKey == null)
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "regist_key is empty"
                });
            if (_device.HostType == null)
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "host_type is empty"
                });
            var _result = await _remotePlayService.WakeUpDeviceAsync(
                _device.IpAddress,
                _device.RegistKey,
                _device.HostType
                );
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = _result,
                Message = _result ? "唤醒成功" : "唤醒失败"
            });
        }

        /// <summary>
        /// 获取指定设备的串流设置与可用选项
        /// </summary>
        [HttpGet("device-settings/{deviceId}")]
        [Authorize]
        public async Task<ActionResult> GetDeviceSettings(string deviceId, CancellationToken cancellationToken)
        {
            try
            {
                var userId = GetCurrentUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未授权"
                    });
                }

                var response = await _deviceSettingsService.GetDeviceSettingsAsync(userId, deviceId, cancellationToken);

                return Ok(new ApiSuccessResponse<DeviceSettingsResponse>
                {
                    Success = true,
                    Data = response,
                    Message = "设备设置加载成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载设备设置失败");
                if (ex is InvalidOperationException)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }

                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "加载设备设置失败: " + ex.Message
                });
            }
        }

        /// <summary>
        /// 更新指定设备的串流设置
        /// </summary>
        [HttpPost("device-settings/{deviceId}")]
        [Authorize]
        public async Task<ActionResult> UpdateDeviceSettings(string deviceId, [FromBody] UpdateDeviceSettingsRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "请求体不能为空"
                });
            }

            try
            {
                var userId = GetCurrentUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未授权"
                    });
                }

                var response = await _deviceSettingsService.UpdateDeviceSettingsAsync(userId, deviceId, request, cancellationToken);

                return Ok(new ApiSuccessResponse<DeviceSettingsResponse>
                {
                    Success = true,
                    Data = response,
                    Message = "设备设置保存成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存设备设置失败");
                if (ex is InvalidOperationException)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }

                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "保存设备设置失败: " + ex.Message
                });
            }
        }
        /// <summary>
        /// 启动会话
        /// 注意：会话创建后会自动启动流并连接控制器（可通过 SessionStartOptions 配置）
        /// </summary>
        /// <param name="hostId">console host_id</param>
        /// <returns>session</returns>
        [HttpPost("start-session")]
        [Authorize]
        public async Task<ActionResult> StartSession(string hostId)
        {
            var userId = GetCurrentUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "未授权"
                });
            }

            var _device = await _rpContext.PSDevices
                 .AsNoTracking()
                 .Where(x => x.HostId == hostId && x.IsRegistered == true)
                 .FirstOrDefaultAsync();
            if (_device == null)
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备未找到"
                });
            if (_device.IpAddress == null)
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "ip address is empty"
                });
            if (_device.HostType == null)
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "host_type is empty"
                });

            // 先检查是否已存在活跃的 session
            var existingSessions = await _sessionService.ListSessionsAsync();
            var existingSession = existingSessions
                .FirstOrDefault(s => s.HostId == hostId && s.IsActive);

            RemoteSession _session;
            if (existingSession != null)
            {
                // 返回已存在的活跃 session
                _session = existingSession;
            }
            else
            {
                // 创建新的 session
                var deviceSettings = await _deviceSettingsService.GetEffectiveSettingsAsync(userId, _device.Id, HttpContext.RequestAborted);

                var sessionOptions = new SessionStartOptions
                {
                    Resolution = deviceSettings.Resolution,
                    Fps = deviceSettings.FrameRate,
                    Quality = deviceSettings.Quality,
                    Bitrate = deviceSettings.Bitrate,
                    StreamType = deviceSettings.StreamType,
                    AutoStartStream = true,
                    AutoConnectController = true
                };

                _session = await _sessionService.StartSessionAsync(
                    _device.IpAddress,
                    new()
                    {
                        HostId = _device.HostId,
                        HostName = _device.HostName,
                        HostIp = _device.IpAddress,
                        RegistrationKey = Convert.FromHexString(_device.RegistKey ?? string.Empty),
                        ServerKey = Convert.FromHexString(_device.RPKey ?? string.Empty),
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddDays(30)
                    },
                    _device.HostType,
                    sessionOptions);
            }

            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = _session,
                Message = "会话启动成功"
            });
        }

        /// <summary>
        /// stop session
        /// </summary>
        /// <param name="sessionId">session id</param>
        /// <returns>status</returns>
        [HttpPost("stop-session")]
        public async Task<ActionResult> StopSession(Guid sessionId)
        {
            var _session = await _sessionService.StopSessionAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = _session,
                Message = _session ? "会话停止成功" : "会话停止失败"
            });
        }

        /// <summary>
        /// start stream (test or normal)
        /// </summary>
        [HttpPost("start-stream")]
        public async Task<ActionResult> StartStream(Guid sessionId, bool test = true)
        {
            var ok = await _streamingService.StartStreamAsync(sessionId, test);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = ok,
                Message = ok ? "流启动成功" : "流启动失败"
            });
        }

        /// <summary>
        /// stop stream
        /// </summary>
        [HttpPost("stop-stream")]
        public async Task<ActionResult> StopStream(Guid sessionId)
        {
            var ok = await _streamingService.StopStreamAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = ok,
                Message = ok ? "流停止成功" : "流停止失败"
            });
        }

        /// <summary>
        /// attach default receiver (for debugging)
        /// </summary>
        [HttpPost("attach-receiver")]
        public async Task<ActionResult> AttachReceiver(Guid sessionId)
        {
            var receiver = new RemotePlay.Services.Streaming.Receiver.DefaultReceiver(_loggerFactory.CreateLogger<DefaultReceiver>());
            var ok = await _streamingService.AttachReceiverAsync(sessionId, receiver);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = ok,
                Message = ok ? "接收器附加成功" : "接收器附加失败"
            });
        }

        /// <summary>
        /// attach file dump receiver (write raw ES to files)
        /// </summary>
        [HttpPost("attach-file-receiver")]
        public async Task<ActionResult> AttachFileReceiver(Guid sessionId, string? videoFile = null, string? audioFile = null)
        {
            var receiver = new RemotePlay.Services.Streaming.Receiver.FileDumpReceiver(videoFile, audioFile);
            var ok = await _streamingService.AttachReceiverAsync(sessionId, receiver);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = ok,
                Message = ok ? "文件接收器附加成功" : "文件接收器附加失败"
            });
        }

        /// <summary>
        /// attach ffplay receiver (preview video using ffplay)
        /// </summary>
        [HttpPost("attach-ffplay-receiver")]
        public async Task<ActionResult> AttachFfplayReceiver(Guid sessionId, string? ffplayPath = null, string? extraArgs = null)
        {
            var receiver = new RemotePlay.Services.Streaming.Receiver.FfplayVideoReceiver(ffplayPath, extraArgs);
            var ok = await _streamingService.AttachReceiverAsync(sessionId, receiver);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = ok,
                Message = ok ? "FFplay接收器附加成功" : "FFplay接收器附加失败"
            });
        }

        /// <summary>
        /// attach ffmpeg mux receiver (transcode/remux to file or stream)
        /// </summary>
        [HttpPost("attach-ffmpeg-receiver")]
        public async Task<ActionResult> AttachFfmpegReceiver(
            Guid sessionId,
            string? output = null,
            bool enableAudio = true,
            string? ffmpegPath = null,
            string? extraArgs = null,
            bool useTcp = true,
            string? videoCodec = null,
            string? audioCodec = null,
            bool genPts = true,
            bool enableVideo = true)
        {
            var receiver = new RemotePlay.Services.Streaming.Receiver.FfmpegMuxReceiver(output, enableAudio, ffmpegPath, extraArgs, useTcp, videoCodec, audioCodec, genPts, enableVideo);
            var ok = await _streamingService.AttachReceiverAsync(sessionId, receiver);
            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = new { success = ok, output },
                Message = ok ? "FFmpeg接收器附加成功" : "FFmpeg接收器附加失败"
            });
        }

        /// <summary>
        /// 一键开启 HLS 联机分享，返回可访问的 m3u8 地址
        /// </summary>
        [HttpPost("share-hls")]
        public async Task<ActionResult> ShareHls(
            Guid sessionId,
            int segmentTime = 2,
            int listSize = 6,
            bool enableAudio = false,
            bool useTcp = true,
            bool enableVideo = true)  // 是否输出视频（false = 纯音频模式，节省带宽）
        {
            _logger.LogInformation("开始设置 HLS 推流 - SessionId: {SessionId}, TCP: {UseTcp}, Video: {Video}, Audio: {Audio}", 
                sessionId, useTcp, enableVideo, enableVideo ? enableAudio : true);

            // 使用 WebRoot 确保静态文件中可见
            var webRoot = _env.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            }
            Directory.CreateDirectory(webRoot);
            var absDir = Path.Combine(webRoot, "hls", sessionId.ToString("N"));
            Directory.CreateDirectory(absDir);
            var m3u8 = Path.Combine(absDir, "index.m3u8");

            _logger.LogInformation("📁 HLS 输出目录: {Dir}", absDir);
            _logger.LogInformation("📋 M3U8 文件: {M3u8}", m3u8);

            // 构造 HLS 额外参数
            var segPattern = Path.Combine(absDir, "seg_%05d.ts");
            // 添加错误容忍参数：
            // -err_detect ignore_err: 忽略解码错误
            // -max_muxing_queue_size 9999: 增大缓冲队列
            // -fps_mode passthrough: 保持原始帧率，不丢弃帧
            var extraArgs = $"-err_detect ignore_err -max_muxing_queue_size 9999 -fps_mode passthrough -f hls -hls_time {segmentTime} -hls_list_size {listSize} -hls_flags delete_segments+program_date_time -hls_segment_filename '{segPattern.Replace("'", "'\\''")}'";

            var ffmpegLogger = _loggerFactory.CreateLogger<FfmpegMuxReceiver>();
            var receiver = new RemotePlay.Services.Streaming.Receiver.FfmpegMuxReceiver(
                output: m3u8,
                enableAudio: enableVideo ? enableAudio : true,  // 纯音频模式强制启用音频
                ffmpegPath: null,
                extraArgs: extraArgs,
                useTcp: useTcp,
                videoCodec: null,
                audioCodec: null,
                genPts: true,
                enableVideo: enableVideo,
                logger: ffmpegLogger);

            var ok = await _streamingService.AttachReceiverAsync(sessionId, receiver);
            if (!ok)
            {
                _logger.LogError("❌ 绑定 HLS 接收器失败 - SessionId: {SessionId}", sessionId);
                return Ok(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "绑定 HLS 接收器失败，请确认流已启动或稍后重试"
                });
            }

            _logger.LogInformation("✅ HLS 接收器已绑定成功");

            // 返回可直接访问的 URL（静态文件已启用）
            var publicUrl = $"/hls/{sessionId:N}/index.m3u8";
            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = new { url = publicUrl },
                Message = "HLS 分享已启动"
            });
        }

        /// <summary>
        /// get session
        /// </summary>
        /// <param name="sessionId">session id</param>
        /// <returns>session</returns>
        [HttpGet("get-session")]
        public async Task<ActionResult> GetSession(Guid sessionId)
        {
            var _session = await _sessionService.GetSessionAsync(sessionId);
            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = _session,
                Message = "获取会话成功"
            });
        }
        /// <summary>
        /// 注册设备到PlayStation主机
        /// </summary>
        /// <param name="request">注册请求</param>
        /// <returns>注册结果</returns>
        [HttpPost("register")]
        public async Task<ActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.HostIp))
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "主机IP不能为空"
                    });

                if (string.IsNullOrEmpty(request.AccountId))
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "账户ID不能为空"
                    });

                if (string.IsNullOrEmpty(request.Pin))
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "PIN不能为空"
                    });

                _logger.LogInformation("开始设备注册 - 主机: {HostIp}, 账户: {AccountId}", request.HostIp, request.AccountId);

                var result = await _remotePlayService.RegisterDeviceAsync(request.HostIp, request.AccountId, request.Pin);

                if (result.Success)
                {
                    return Ok(new ApiSuccessResponse<object>
                    {
                        Success = true,
                        Data = result,
                        Message = "设备注册成功"
                    });
                }
                else
                {
                    return Ok(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage ?? "设备注册失败"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备注册失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备注册失败: " + ex.Message
                });
            }
        }

        /// <summary>
        /// 验证设备凭据
        /// </summary>
        /// <param name="credentials">设备凭据</param>
        /// <returns>验证结果</returns>
        [HttpPost("validate-credentials")]
        public async Task<ActionResult> ValidateCredentials([FromBody] DeviceCredentials credentials)
        {
            try
            {
                if (credentials == null)
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "凭据不能为空"
                    });

                _logger.LogInformation("验证设备凭据 - 主机: {HostName}", credentials.HostName);

                var isValid = await _remotePlayService.ValidateCredentialsAsync(credentials);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = isValid,
                    Message = isValid ? "凭据有效" : "凭据无效或已过期"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证凭据失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "验证凭据失败: " + ex.Message
                });
            }
        }

        [HttpGet("test")]
        public ActionResult TestEncoding(string hostType, string hostIp, string psnId, string pin)
        {
            var (chsper, headers, playload) = _reg.GetRegistCipherHeadersPayload(hostType, hostIp, psnId, pin);

            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = new
                {
                    chsper = chsper,
                    headers = headers,
                    playload = playload
                },
                Message = "测试编码成功"
            });
        }

        #region 控制器相关接口

        /// <summary>
        /// 连接控制器到会话
        /// </summary>
        [HttpPost("controller/connect")]
        public async Task<ActionResult> ConnectController(Guid sessionId)
        {
            var success = await _controllerService.ConnectAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = success,
                Message = success ? "控制器连接成功" : "控制器连接失败"
            });
        }

        /// <summary>
        /// 断开控制器连接
        /// </summary>
        [HttpPost("controller/disconnect")]
        public async Task<ActionResult> DisconnectController(Guid sessionId)
        {
            await _controllerService.DisconnectAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = true,
                Message = "控制器断开成功"
            });
        }

        /// <summary>
        /// 启动控制器（开始自动发送摇杆状态）
        /// </summary>
        [HttpPost("controller/start")]
        public async Task<ActionResult> StartController(Guid sessionId)
        {
            var success = await _controllerService.StartAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = success,
                Message = success ? "控制器启动成功" : "控制器启动失败"
            });
        }

        /// <summary>
        /// 停止控制器
        /// </summary>
        [HttpPost("controller/stop")]
        public async Task<ActionResult> StopController(Guid sessionId)
        {
            await _controllerService.StopAsync(sessionId);
            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = true,
                Message = "控制器停止成功"
            });
        }

        /// <summary>
        /// 按键操作
        /// </summary>
        [HttpPost("controller/button")]
        public async Task<ActionResult> ControllerButton(
            [FromBody] ControllerButtonRequest request)
        {
            if (!Enum.TryParse<RemotePlay.Services.Streaming.FeedbackEvent.ButtonType>(
                request.Button.ToUpper(), out var buttonType))
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = $"无效的按键: {request.Button}，可用按键: {string.Join(", ", _controllerService.GetAvailableButtons())}"
                });
            }

            var action = request.Action?.ToLower() switch
            {
                "press" => IControllerService.ButtonAction.PRESS,
                "release" => IControllerService.ButtonAction.RELEASE,
                "tap" => IControllerService.ButtonAction.TAP,
                _ => IControllerService.ButtonAction.TAP
            };

            await _controllerService.ButtonAsync(
                request.SessionId,
                buttonType,
                action,
                request.DelayMs ?? 100);

            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = true,
                Message = "按键操作成功"
            });
        }

        /// <summary>
        /// 设置摇杆状态
        /// </summary>
        [HttpPost("controller/stick")]
        public async Task<ActionResult> ControllerStick(
            [FromBody] ControllerStickRequest request)
        {
            try
            {
                (float x, float y)? point = null;
                if (request.Point != null)
                {
                    point = (request.Point.X, request.Point.Y);
                }

                await _controllerService.StickAsync(
                    request.SessionId,
                    request.StickName,
                    request.Axis,
                    request.Value,
                    point);

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "摇杆设置成功"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 设置扳机压力
        /// </summary>
        [HttpPost("controller/trigger")]
        public async Task<ActionResult> ControllerTrigger(
            [FromBody] ControllerTriggerRequest request)
        {
            if (request == null || (!request.L2.HasValue && !request.R2.HasValue))
            {
                return BadRequest(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "必须至少提供 L2 或 R2 的数值"
                });
            }

            await _controllerService.SetTriggersAsync(request.SessionId, request.L2, request.R2);

            return Ok(new ApiSuccessResponse<bool>
            {
                Success = true,
                Data = true,
                Message = "扳机压力设置成功"
            });
        }

        /// <summary>
        /// 获取当前摇杆状态
        /// </summary>
        [HttpGet("controller/state")]
        public ActionResult GetControllerState(Guid sessionId)
        {
            var state = _controllerService.GetStickState(sessionId);
            if (state == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "控制器未连接"
                });
            }

            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = new
                {
                    left = new { x = state.Left.X, y = state.Left.Y },
                    right = new { x = state.Right.X, y = state.Right.Y },
                    triggers = new { l2 = state.L2State / 255f, r2 = state.R2State / 255f }
                },
                Message = "获取控制器状态成功"
            });
        }

        /// <summary>
        /// 获取所有可用按键
        /// </summary>
        [HttpGet("controller/buttons")]
        public ActionResult GetAvailableButtons()
        {
            var buttons = _controllerService.GetAvailableButtons();
            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = buttons,
                Message = "获取可用按键成功"
            });
        }

        /// <summary>
        /// 检查控制器状态
        /// </summary>
        [HttpGet("controller/status")]
        public ActionResult GetControllerStatus(Guid sessionId)
        {
            return Ok(new ApiSuccessResponse<object>
            {
                Success = true,
                Data = new
                {
                    isRunning = _controllerService.IsRunning(sessionId),
                    isReady = _controllerService.IsReady(sessionId)
                },
                Message = "获取控制器状态成功"
            });
        }

        #endregion

        private string? GetCurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        #region 设备绑定相关接口

        /// <summary>
        /// 绑定PS主机到当前用户
        /// </summary>
        /// <param name="request">绑定请求</param>
        /// <returns>绑定结果</returns>
        [HttpPost("bind")]
        [Authorize]
        public async Task<ActionResult> BindDevice([FromBody] BindDeviceRequest request)
        {
            try
            {
                // 获取当前用户ID
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未授权"
                    });
                }

                // 验证输入参数
                if (string.IsNullOrEmpty(request.HostIp))
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "主机IP不能为空"
                    });

                // 如果提供了账户ID和PIN，则进行注册
                RegisterResult? registerResult = null;
                if (!string.IsNullOrEmpty(request.AccountId) && !string.IsNullOrEmpty(request.Pin))
                {
                    _logger.LogInformation("开始设备注册 - 主机: {HostIp}, 账户: {AccountId}", request.HostIp, request.AccountId);
                    registerResult = await _remotePlayService.RegisterDeviceAsync(request.HostIp, request.AccountId, request.Pin);
                    
                    if (!registerResult.Success)
                    {
                        return Ok(new ApiErrorResponse
                        {
                            Success = false,
                            ErrorMessage = "设备注册失败: " + registerResult.ErrorMessage
                        });
                    }
                }

                // 发现设备获取详细信息
                ConsoleInfo? deviceInfo = await _remotePlayService.DiscoverDeviceAsync(request.HostIp);

                if (deviceInfo == null)
                {
                    return BadRequest(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "无法发现设备，请确保设备已开机并连接到同一网络"
                    });
                }

                // 检查设备是否已存在
                var existingDevice = await _rpContext.PSDevices
                    .FirstOrDefaultAsync(d => d.HostId == deviceInfo.Uuid);

                Models.DB.PlayStation.Device device;
                if (existingDevice != null)
                {
                    // 更新现有设备信息
                    device = existingDevice;
                    device.IpAddress = deviceInfo.Ip;
                    device.HostName = deviceInfo.Name;
                    device.HostType = deviceInfo.HostType;
                    device.SystemVersion = deviceInfo.SystemVerion;
                    device.DiscoverProtocolVersion = deviceInfo.DeviceDiscoverPotocolVersion;
                    device.Status = "OK";

                    // 如果注册成功，更新注册信息
                    if (registerResult != null && registerResult.Success)
                    {
                        device.IsRegistered = true;
                        device.APBssid = registerResult?.RegistData?.GetValueOrDefault("AP-Bssid");
                        device.RegistData = JObject.FromObject(registerResult?.RegistData ?? new() { });
                        device.RegistKey = registerResult?.RegistData?.FirstOrDefault(x => x.Key.Contains("RegistKey")).Value;
                        device.MacAddress = registerResult?.RegistData?.FirstOrDefault(x => x.Key.Contains("Mac")).Value;
                        device.RPKeyType = registerResult?.RegistData?.GetValueOrDefault("RP-KeyType");
                        device.RPKey = registerResult?.RegistData?.GetValueOrDefault("RP-Key");
                    }
                }
                else
                {
                    // 创建新设备
                    device = new Models.DB.PlayStation.Device
                    {
                        Id = _idGenerator.NextStringId(),
                        uuid = Guid.NewGuid(),
                        HostId = deviceInfo.Uuid,
                        HostName = deviceInfo.Name,
                        HostType = deviceInfo.HostType,
                        IpAddress = deviceInfo.Ip,
                        SystemVersion = deviceInfo.SystemVerion,
                        DiscoverProtocolVersion = deviceInfo.DeviceDiscoverPotocolVersion,
                        Status = deviceInfo.status,
                        IsRegistered = registerResult?.Success ?? false,
                        APBssid = registerResult?.RegistData?.GetValueOrDefault("AP-Bssid"),
                        RegistData = JObject.FromObject(registerResult?.RegistData ?? new() { }),
                        RegistKey = registerResult?.RegistData?.FirstOrDefault(x => x.Key.Contains("RegistKey")).Value,
                        MacAddress = registerResult?.RegistData?.FirstOrDefault(x => x.Key.Contains("Mac")).Value,
                        RPKeyType = registerResult?.RegistData?.GetValueOrDefault("RP-KeyType"),
                        RPKey = registerResult?.RegistData?.GetValueOrDefault("RP-Key"),

                    };
                    _rpContext.PSDevices.Add(device);
                }

                await _rpContext.SaveChangesAsync();

                // 检查用户是否已绑定此设备
                var existingUserDevice = await _rpContext.UserDevices
                    .FirstOrDefaultAsync(ud => ud.UserId == userIdClaim && ud.DeviceId == device.Id);

                if (existingUserDevice == null)
                {
                    // 创建用户设备关联
                    var userDevice = new Models.DB.Auth.UserDevice
                    {
                        Id = Guid.NewGuid().ToString(),
                        UserId = userIdClaim,
                        DeviceId = device.Id,
                        DeviceName = request.DeviceName ?? device.HostName,
                        DeviceType = device.HostType,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    };
                    _rpContext.UserDevices.Add(userDevice);
                    await _rpContext.SaveChangesAsync();
                }
                else
                {
                    // 如果已存在但未激活，则激活
                    if (!existingUserDevice.IsActive)
                    {
                        existingUserDevice.IsActive = true;
                        existingUserDevice.UpdatedAt = DateTime.UtcNow;
                        await _rpContext.SaveChangesAsync();
                    }
                }

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = new
                    {
                        deviceId = device.Id,
                        hostId = device.HostId,
                        hostName = device.HostName,
                        hostType = device.HostType,
                        ipAddress = device.IpAddress,
                        isRegistered = device.IsRegistered
                    },
                    Message = "设备绑定成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备绑定失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备绑定失败: " + ex.Message
                });
            }
        }

        /// <summary>
        /// 获取当前用户已绑定的设备列表
        /// </summary>
        /// <returns>设备列表</returns>
        [HttpGet("my-devices")]
        [Authorize]
        public async Task<ActionResult> GetMyDevices()
        {
            try
            {
                var userId = GetCurrentUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new
                    {
                        success = false,
                        statusCode = 401,
                        message = "未授权"
                    });
                }

                var devices = await _deviceSettingsService.GetUserDevicesAsync(userId, HttpContext.RequestAborted);

                return Ok(new ApiSuccessResponse<object>
                {
                    Success = true,
                    Data = devices,
                    Message = $"找到 {devices.Count} 个已绑定的设备"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户设备列表失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "获取设备列表失败: " + ex.Message
                });
            }
        }

        /// <summary>
        /// 解绑设备
        /// </summary>
        /// <param name="userDeviceId">用户设备关联ID</param>
        /// <returns>解绑结果</returns>
        [HttpPost("unbind")]
        [Authorize]
        public async Task<ActionResult> UnbindDevice(string userDeviceId)
        {
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return Unauthorized(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未授权"
                    });
                }

                var userDevice = await _rpContext.UserDevices
                    .FirstOrDefaultAsync(ud => ud.Id == userDeviceId && ud.UserId == userIdClaim);

                if (userDevice == null)
                {
                    return NotFound(new ApiErrorResponse
                    {
                        Success = false,
                        ErrorMessage = "未找到该设备绑定"
                    });
                }

                userDevice.IsActive = false;
                userDevice.UpdatedAt = DateTime.UtcNow;
                await _rpContext.SaveChangesAsync();

                return Ok(new ApiSuccessResponse<bool>
                {
                    Success = true,
                    Data = true,
                    Message = "设备解绑成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备解绑失败");
                return StatusCode(500, new ApiErrorResponse
                {
                    Success = false,
                    ErrorMessage = "设备解绑失败: " + ex.Message
                });
            }
        }

        #endregion
    }

    public class ControllerButtonRequest
    {
        public Guid SessionId { get; set; }
        public string Button { get; set; } = string.Empty;
        public string? Action { get; set; } = "tap";  // press, release, tap
        public int? DelayMs { get; set; } = 100;
    }

    public class ControllerStickRequest
    {
        public Guid SessionId { get; set; }
        public string StickName { get; set; } = string.Empty;  // left, right
        public string? Axis { get; set; }  // x, y
        public float? Value { get; set; }  // -1.0 to 1.0
        public StickPoint? Point { get; set; }
    }

    public class StickPoint
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    public class RegisterDeviceRequest
    {
        public string HostIp { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
    }

    public class BindDeviceRequest
    {
        public string HostIp { get; set; } = string.Empty;
        public string? AccountId { get; set; }
        public string? Pin { get; set; }
        public string? DeviceName { get; set; }
    }

}
