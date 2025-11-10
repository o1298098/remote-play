using Microsoft.AspNetCore.SignalR;
using RemotePlay.Contracts.Services;
using RemotePlay.Services.Streaming;

namespace RemotePlay.Hubs
{
    /// <summary>
    /// SignalR Hub for low-latency controller input
    /// 用于低延迟控制器输入的SignalR Hub
    /// </summary>
    public class ControllerHub : Hub
    {
        private readonly IControllerService _controllerService;
        private readonly ILogger<ControllerHub> _logger;

        public ControllerHub(
            IControllerService controllerService,
            ILogger<ControllerHub> logger)
        {
            _controllerService = controllerService;
            _logger = logger;
        }

        /// <summary>
        /// 连接控制器到会话
        /// </summary>
        public async Task ConnectController(Guid sessionId)
        {
            try
            {
                var success = await _controllerService.ConnectAsync(sessionId);
                if (success)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(sessionId));
                    await Clients.Caller.SendAsync("ControllerConnected", true);
                    _logger.LogInformation("控制器通过SignalR连接: SessionId={SessionId}, ConnectionId={ConnectionId}", 
                        sessionId, Context.ConnectionId);
                }
                else
                {
                    // 如果连接失败，可能是已经连接过了，检查控制器状态
                    var isReady = _controllerService.IsReady(sessionId);
                    var isRunning = _controllerService.IsRunning(sessionId);
                    
                    if (isReady || isRunning)
                    {
                        // 控制器已经存在且可用，认为是成功（可能是其他连接或之前的连接）
                        await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupName(sessionId));
                        await Clients.Caller.SendAsync("ControllerConnected", true);
                        _logger.LogInformation("控制器已存在且可用，视为连接成功: SessionId={SessionId}, ConnectionId={ConnectionId}, Ready={Ready}, Running={Running}", 
                            sessionId, Context.ConnectionId, isReady, isRunning);
                    }
                    else
                    {
                        // 真正的连接失败
                        await Clients.Caller.SendAsync("ControllerConnected", false);
                        await Clients.Caller.SendAsync("Error", "控制器连接失败：会话可能不存在或已失效");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接控制器时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("ControllerConnected", false);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 断开控制器连接
        /// </summary>
        public async Task DisconnectController(Guid sessionId)
        {
            try
            {
                await _controllerService.DisconnectAsync(sessionId);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupName(sessionId));
                await Clients.Caller.SendAsync("ControllerDisconnected", true);
                _logger.LogInformation("控制器通过SignalR断开: SessionId={SessionId}, ConnectionId={ConnectionId}", 
                    sessionId, Context.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "断开控制器时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 启动控制器（开始自动发送摇杆状态）
        /// </summary>
        public async Task StartController(Guid sessionId)
        {
            try
            {
                var success = await _controllerService.StartAsync(sessionId);
                await Clients.Caller.SendAsync("ControllerStarted", success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动控制器时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 停止控制器
        /// </summary>
        public async Task StopController(Guid sessionId)
        {
            try
            {
                await _controllerService.StopAsync(sessionId);
                await Clients.Caller.SendAsync("ControllerStopped", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止控制器时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 按键操作（低延迟版本）
        /// </summary>
        public async Task Button(Guid sessionId, string button, string? action = "tap", int? delayMs = 100)
        {
            try
            {
                if (!Enum.TryParse<FeedbackEvent.ButtonType>(button.ToUpper(), out var buttonType))
                {
                    await Clients.Caller.SendAsync("Error", $"无效的按键: {button}");
                    return;
                }

                var buttonAction = action?.ToLower() switch
                {
                    "press" => IControllerService.ButtonAction.PRESS,
                    "release" => IControllerService.ButtonAction.RELEASE,
                    "tap" => IControllerService.ButtonAction.TAP,
                    _ => IControllerService.ButtonAction.TAP
                };

                await _controllerService.ButtonAsync(
                    sessionId,
                    buttonType,
                    buttonAction,
                    delayMs ?? 100);

                // 不等待响应，直接返回成功（降低延迟）
                await Clients.Caller.SendAsync("ButtonSent", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送按键时出错: SessionId={SessionId}, Button={Button}", sessionId, button);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 设置摇杆状态（低延迟版本）
        /// </summary>
        public async Task Stick(Guid sessionId, string stickName, string? axis = null, float? value = null, float? x = null, float? y = null)
        {
            try
            {
                (float x, float y)? point = null;
                if (x.HasValue && y.HasValue)
                {
                    point = (x.Value, y.Value);
                }

                await _controllerService.StickAsync(
                    sessionId,
                    stickName,
                    axis,
                    value,
                    point);

                // 不等待响应，直接返回成功（降低延迟）
                await Clients.Caller.SendAsync("StickSent", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置摇杆时出错: SessionId={SessionId}, StickName={StickName}", sessionId, stickName);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 设置扳机压力
        /// </summary>
        public async Task SetTriggers(Guid sessionId, float? l2 = null, float? r2 = null)
        {
            try
            {
                await _controllerService.SetTriggersAsync(sessionId, l2, r2);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置扳机时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 获取当前摇杆状态
        /// </summary>
        public async Task GetStickState(Guid sessionId)
        {
            try
            {
                var state = _controllerService.GetStickState(sessionId);
                if (state == null)
                {
                    await Clients.Caller.SendAsync("StickState", null);
                    return;
                }

                await Clients.Caller.SendAsync("StickState", new
                {
                    left = new { x = state.Left.X, y = state.Left.Y },
                    right = new { x = state.Right.X, y = state.Right.Y }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取摇杆状态时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 获取控制器状态
        /// </summary>
        public async Task GetControllerStatus(Guid sessionId)
        {
            try
            {
                var status = new
                {
                    isRunning = _controllerService.IsRunning(sessionId),
                    isReady = _controllerService.IsReady(sessionId)
                };
                await Clients.Caller.SendAsync("ControllerStatus", status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取控制器状态时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 获取所有可用按键
        /// </summary>
        public async Task GetAvailableButtons()
        {
            try
            {
                var buttons = _controllerService.GetAvailableButtons();
                await Clients.Caller.SendAsync("AvailableButtons", buttons);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可用按键时出错");
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 设置左摇杆（便捷方法）
        /// </summary>
        public async Task SetLeftStick(Guid sessionId, float x, float y)
        {
            try
            {
                await _controllerService.StickAsync(sessionId, "left", null, null, (x, y));
                await Clients.Caller.SendAsync("StickSent", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置左摇杆时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 设置右摇杆（便捷方法）
        /// </summary>
        public async Task SetRightStick(Guid sessionId, float x, float y)
        {
            try
            {
                await _controllerService.StickAsync(sessionId, "right", null, null, (x, y));
                await Clients.Caller.SendAsync("StickSent", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置右摇杆时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 批量发送摇杆更新（用于高频更新，减少网络往返）
        /// 支持同时更新左右摇杆，如果某个轴未提供则保持当前值
        /// </summary>
        public async Task BatchStickUpdate(Guid sessionId, float? leftX = null, float? leftY = null, float? rightX = null, float? rightY = null)
        {
            try
            {
                // 获取当前状态（只在需要时获取一次）
                var currentState = _controllerService.GetStickState(sessionId);
                
                // 更新左摇杆（如果提供了任何值）
                if (leftX.HasValue || leftY.HasValue)
                {
                    var newLeftX = leftX ?? currentState?.Left.X ?? 0f;
                    var newLeftY = leftY ?? currentState?.Left.Y ?? 0f;
                    await _controllerService.StickAsync(sessionId, "left", null, null, (newLeftX, newLeftY));
                }

                // 更新右摇杆（如果提供了任何值）
                if (rightX.HasValue || rightY.HasValue)
                {
                    var newRightX = rightX ?? currentState?.Right.X ?? 0f;
                    var newRightY = rightY ?? currentState?.Right.Y ?? 0f;
                    await _controllerService.StickAsync(sessionId, "right", null, null, (newRightX, newRightY));
                }

                // 不等待响应，直接返回成功（降低延迟）
                await Clients.Caller.SendAsync("BatchStickSent", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新摇杆时出错: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        /// <summary>
        /// 同时设置左右摇杆（最便捷的方法）
        /// </summary>
        public async Task SetSticks(Guid sessionId, float leftX, float leftY, float rightX, float rightY)
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace(
                        "SignalR SetSticks received: SessionId={SessionId} Left=({LeftX:F4},{LeftY:F4}) Right=({RightX:F4},{RightY:F4})",
                        sessionId,
                        leftX,
                        leftY,
                        rightX,
                        rightY);
                }

                // 并行设置左右摇杆（提高性能）
                await Task.WhenAll(
                    _controllerService.StickAsync(sessionId, "left", null, null, (leftX, leftY)),
                    _controllerService.StickAsync(sessionId, "right", null, null, (rightX, rightY))
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置摇杆时出错: SessionId={SessionId}", sessionId);
                _ = Clients.Caller.SendAsync("Error", ex.Message);
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception != null)
            {
                _logger.LogWarning(exception, "SignalR连接断开: ConnectionId={ConnectionId}", Context.ConnectionId);
            }
            await base.OnDisconnectedAsync(exception);
        }

        private static string GetGroupName(Guid sessionId) => $"session_{sessionId}";
    }
}

