using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RemotePlay.Contracts.Services;
using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Core;
using RemotePlay.Services.Streaming.Controller;
using RemotePlay.Hubs;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RemotePlay.Services.Controller
{
    /// <summary>
    /// 控制器服务实现
    /// 对应Python的Controller类
    /// </summary>
    public class ControllerService : IControllerService
    {
        private readonly ILogger<ControllerService> _logger;
        private readonly IStreamingService _streamingService;
		private readonly IHubContext<ControllerHub> _controllerHubContext;
        
        // 每个会话的控制器实例
        private readonly ConcurrentDictionary<Guid, ControllerInstance> _controllers = new();

        // 状态发送间隔配置
        private const int StateIntervalMaxMs = 16;
        private const int StateIntervalMinMs = 8;
        private const int MaxEvents = 5;

		public ControllerService(
			ILogger<ControllerService> logger,
			IStreamingService streamingService,
			IHubContext<ControllerHub> controllerHubContext)
        {
            _logger = logger;
            _streamingService = streamingService;
			_controllerHubContext = controllerHubContext ?? throw new ArgumentNullException(nameof(controllerHubContext));
        }

        public async Task<bool> ConnectAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (_controllers.ContainsKey(sessionId))
            {
                _logger.LogWarning("控制器已连接到会话 {SessionId}，请先断开连接", sessionId);
                return false;
            }

            var session = await _streamingService.GetSessionAsync(sessionId);
            if (session == null)
            {
                _logger.LogError("会话 {SessionId} 不存在", sessionId);
                return false;
            }

			var controller = new ControllerInstance(sessionId, this, _logger, _streamingService);
			_controllers[sessionId] = controller;

			var stream = await _streamingService.GetStreamAsync(sessionId);
			if (stream != null)
			{
				controller.AttachStream(stream);
			}
            
            _logger.LogInformation("控制器已连接到会话 {SessionId}", sessionId);
            return true;
        }

        public Task DisconnectAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (_controllers.TryRemove(sessionId, out var controller))
            {
                controller.Stop();
                _logger.LogInformation("控制器已从会话 {SessionId} 断开", sessionId);
            }
            return Task.CompletedTask;
        }

        public Task<bool> StartAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                _logger.LogWarning("会话 {SessionId} 没有连接的控制器", sessionId);
                return Task.FromResult(false);
            }

            controller.Start();
            return Task.FromResult(true);
        }

        public Task StopAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (_controllers.TryGetValue(sessionId, out var controller))
            {
                controller.Stop();
            }
            return Task.CompletedTask;
        }

        public async Task ButtonAsync(
            Guid sessionId,
            FeedbackEvent.ButtonType button,
            IControllerService.ButtonAction action = IControllerService.ButtonAction.TAP,
            int delayMs = 100,
            CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                _logger.LogWarning("会话 {SessionId} 没有连接的控制器", sessionId);
                return;
            }

            await controller.ButtonAsync(button, action, delayMs, ct);
        }

        public Task StickAsync(
            Guid sessionId,
            string stickName,
            string? axis = null,
            float? value = null,
            (float x, float y)? point = null,
            CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                _logger.LogWarning("会话 {SessionId} 没有连接的控制器", sessionId);
                return Task.CompletedTask;
            }

            controller.SetStick(stickName, axis, value, point);
            return Task.CompletedTask;
        }

        public Task SetTriggersAsync(
            Guid sessionId,
            float? l2 = null,
            float? r2 = null,
            CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                _logger.LogWarning("会话 {SessionId} 没有连接的控制器", sessionId);
                return Task.CompletedTask;
            }

            controller.SetTriggers(l2, r2);
            return Task.CompletedTask;
        }

        public Task UpdateSticksAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                return Task.CompletedTask;
            }

            controller.UpdateSticks();
            return Task.CompletedTask;
        }

        public ControllerState? GetStickState(Guid sessionId)
        {
            if (_controllers.TryGetValue(sessionId, out var controller))
            {
                return controller.StickState;
            }
            return null;
        }

        public bool IsRunning(Guid sessionId)
        {
            if (_controllers.TryGetValue(sessionId, out var controller))
            {
                return controller.IsRunning;
            }
            return false;
        }

        public bool IsReady(Guid sessionId)
        {
            if (_controllers.TryGetValue(sessionId, out var controller))
            {
                return controller.IsReady;
            }
            return false;
        }

        public List<string> GetAvailableButtons()
        {
            return Enum.GetNames(typeof(FeedbackEvent.ButtonType)).ToList();
        }

		internal async Task BroadcastRumbleAsync(Guid sessionId, RumbleEventArgs rumble)
		{
			try
			{
				var payload = new
				{
					sessionId,
					rumble.Unknown,
					rawLeft = rumble.Left,
					rawRight = rumble.Right,
					left = rumble.AdjustedLeft,
					right = rumble.AdjustedRight,
					multiplier = rumble.Multiplier,
					ps5RumbleIntensity = rumble.Ps5RumbleIntensity,
					ps5TriggerIntensity = rumble.Ps5TriggerIntensity,
					timestamp = rumble.TimestampUtc
				};

				await _controllerHubContext.Clients
					.Group(GetControllerGroupName(sessionId))
					.SendAsync("ControllerRumble", payload);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to broadcast rumble event for session {SessionId}", sessionId);
			}
		}

		private static string GetControllerGroupName(Guid sessionId) => $"session_{sessionId}";

        /// <summary>
        /// 控制器实例（内部类）
        /// 对应Python的Controller类的实例
        /// </summary>
        private class ControllerInstance
        {
            private readonly Guid _sessionId;
			private readonly ControllerService _owner;
            private readonly ILogger _logger;
            private readonly IStreamingService _streamingService;
			private RPStreamV2? _attachedStream;
			private readonly object _streamSubscriptionLock = new();

            private ushort _sequenceEvent = 0;
            private ushort _sequenceState = 0;
            private readonly LinkedList<byte[]> _eventBuffer = new(); // 使用 LinkedList 以支持在开头插入
            private ControllerState _lastState = new();
            private ControllerState _stickState = new();
            
            private CancellationTokenSource? _cts;
            private Task? _workerTask;
            private readonly SemaphoreSlim _shouldSend = new(0);
            private long _lastUpdateTicks = 0;

            public ControllerState StickState => _stickState;
            public bool IsRunning => _workerTask != null && !_workerTask.IsCompleted;
            public bool IsReady => _streamingService.IsStreamRunningAsync(_sessionId).Result;

			public ControllerInstance(
				Guid sessionId,
				ControllerService owner,
				ILogger logger,
				IStreamingService streamingService)
            {
                _sessionId = sessionId;
				_owner = owner;
                _logger = logger;
                _streamingService = streamingService;
            }

			public void AttachStream(RPStreamV2 stream)
			{
				ArgumentNullException.ThrowIfNull(stream);

				lock (_streamSubscriptionLock)
				{
					if (ReferenceEquals(_attachedStream, stream))
					{
						return;
					}

					if (_attachedStream != null)
					{
						_attachedStream.RumbleReceived -= OnRumbleReceived;
					}

					_attachedStream = stream;
					_attachedStream.RumbleReceived += OnRumbleReceived;
				}
			}

			private void DetachStream()
			{
				lock (_streamSubscriptionLock)
				{
					if (_attachedStream != null)
					{
						_attachedStream.RumbleReceived -= OnRumbleReceived;
						_attachedStream = null;
					}
				}
			}

            public void Start()
            {
                if (_workerTask != null)
                {
                    _logger.LogWarning("控制器已在运行");
                    return;
                }

                _cts = new CancellationTokenSource();
                _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token), _cts.Token);
                _logger.LogDebug("控制器 worker 已启动，会话 {SessionId}", _sessionId);
            }

            public void Stop()
            {
                _cts?.Cancel();
				DetachStream();
                _workerTask?.Wait(TimeSpan.FromSeconds(1));
                _cts?.Dispose();
                _cts = null;
                _workerTask = null;
                _logger.LogInformation("控制器已停止，会话 {SessionId}", _sessionId);
            }

            private async Task WorkerLoopAsync(CancellationToken ct)
            {
                try
                {
                    // 初始等待
                    await _shouldSend.WaitAsync(TimeSpan.FromSeconds(1), ct);

                    while (!ct.IsCancellationRequested)
                    {
                        await _shouldSend.WaitAsync(TimeSpan.FromMilliseconds(StateIntervalMinMs), ct);
                        
                        if (IsReady)
                        {
                            UpdateSticks();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "控制器 worker 循环异常");
                }
            }

            public async Task ButtonAsync(
                FeedbackEvent.ButtonType button,
                IControllerService.ButtonAction action,
                int delayMs,
                CancellationToken ct)
            {
                if (!CheckSession())
                {
                    _logger.LogWarning("无法发送按钮事件: 会话 {SessionId} 的流不存在", _sessionId);
                    return;
                }

                _logger.LogDebug("发送按钮事件: 按钮={Button}, 动作={Action}, 延迟={DelayMs}ms, 会话={SessionId}", 
                    button, action, delayMs, _sessionId);

                // 添加按键事件
                if (action == IControllerService.ButtonAction.PRESS)
                {
                    AddEventBuffer(new FeedbackEvent(button, isActive: true));
                }
                else if (action == IControllerService.ButtonAction.RELEASE)
                {
                    AddEventBuffer(new FeedbackEvent(button, isActive: false));
                }
                else if (action == IControllerService.ButtonAction.TAP)
                {
                    AddEventBuffer(new FeedbackEvent(button, isActive: true));
                }

                await SendEventAsync();

                // 如果是TAP，延迟后发送释放
                if (action == IControllerService.ButtonAction.TAP)
                {
                    await Task.Delay(delayMs, ct);
                    await ButtonAsync(button, IControllerService.ButtonAction.RELEASE, 0, ct);
                }
            }

            public void SetStick(
                string stickName,
                string? axis = null,
                float? value = null,
                (float x, float y)? point = null)
            {
                var lowerName = stickName.ToLower();
                
                if (lowerName != "left" && lowerName != "right")
                {
                    throw new ArgumentException("Invalid stick name. Expected 'left' or 'right'", nameof(stickName));
                }

                // 如果提供了point，优先使用
                if (point.HasValue)
                {
                    var newState = new StickState(point.Value.x, point.Value.y);
                    if (lowerName == "left")
                    {
                        _stickState.Left = newState;
                    }
                    else
                    {
                        _stickState.Right = newState;
                    }

                    _shouldSend.Release();
                    return;
                }

                // 否则使用axis和value
                if (axis == null || value == null)
                {
                    throw new ArgumentException("Axis and Value cannot be null when point is not provided");
                }

                var lowerAxis = axis.ToLower();
                if (lowerAxis != "x" && lowerAxis != "y")
                {
                    throw new ArgumentException("Invalid axis. Expected 'x' or 'y'", nameof(axis));
                }

                var currentStick = lowerName == "left" ? _stickState.Left : _stickState.Right;
                var newX = lowerAxis == "x" ? value.Value : currentStick.X;
                var newY = lowerAxis == "y" ? value.Value : currentStick.Y;
                var newStick = new StickState(newX, newY);

                if (lowerName == "left")
                {
                    _stickState.Left = newStick;
                }
                else
                {
                    _stickState.Right = newStick;
                }


                _shouldSend.Release();
            }

        public void SetTriggers(float? l2, float? r2)
        {
            bool changed = false;

            if (l2.HasValue)
            {
                var value = NormalizeTrigger(l2.Value);
                if (_stickState.L2State != value)
                {
                    _stickState.L2State = value;
                    changed = true;
                }
            }

            if (r2.HasValue)
            {
                var value = NormalizeTrigger(r2.Value);
                if (_stickState.R2State != value)
                {
                    _stickState.R2State = value;
                    changed = true;
                }
            }

            if (changed)
            {
                _shouldSend.Release();
            }
        }

            public void UpdateSticks()
            {
                if (!CheckSession())
                {
                    return;
                }

                var nowTicks = Stopwatch.GetTimestamp();
                var elapsedMs = (nowTicks - _lastUpdateTicks) * 1000.0 / Stopwatch.Frequency;
                var stateChanged = !_stickState.Equals(_lastState);

                if (!stateChanged && elapsedMs < StateIntervalMaxMs)
                {
                    return;
                }

                // 更新最后状态
                _lastState = new ControllerState(_stickState.Left, _stickState.Right);
                _lastUpdateTicks = nowTicks;


                // ✅ 通过 FeedbackSenderService 更新控制器状态（包括摇杆）
                // 这样摇杆数据会通过 FeedbackSenderService 的循环自动发送
                var stream = _streamingService.GetStreamAsync(_sessionId).Result;
                if (stream != null)
                {
                    // 使用 UpdateControllerState 方法，让 FeedbackSenderService 处理发送
                    stream.UpdateControllerState(_stickState);
                }
            }

            private void AddEventBuffer(FeedbackEvent ev)
            {
                var buf = new byte[FeedbackEvent.Length];
                ev.Pack(buf);
                
                // 添加到链表开头（最新的在前）
                _eventBuffer.AddFirst(buf);
                
                // 如果超过最大事件数，移除最旧的事件（在末尾）
                if (_eventBuffer.Count > MaxEvents)
                {
                    _eventBuffer.RemoveLast();
                }
            }

            private async Task SendEventAsync()
            {
                if (_eventBuffer.Count == 0)
                {
                    _logger.LogDebug("事件缓冲区为空，跳过发送");
                    return;
                }

                // 合并所有事件，保持添加顺序
                var eventCount = _eventBuffer.Count;
                var data = _eventBuffer.SelectMany(b => b).ToArray();
                _eventBuffer.Clear();

                var stream = await _streamingService.GetStreamAsync(_sessionId);
                if (stream != null)
                {
					AttachStream(stream);
                    _logger.LogDebug("发送控制器事件到流: 事件数={EventCount}, 数据长度={DataLength}, 序列号={Sequence}, 会话={SessionId}", 
                        eventCount, data.Length, _sequenceEvent, _sessionId);
                    
                    stream.SendFeedback(
                        (int)FeedbackHeaderType.EVENT,
                        _sequenceEvent,
                        data);
                    
                    _sequenceEvent++;
                    _logger.LogDebug("控制器事件已发送，新序列号={Sequence}", _sequenceEvent);
                }
                else
                {
                    _logger.LogWarning("无法发送控制器事件: 流不存在，会话={SessionId}", _sessionId);
                }
            }

			private void OnRumbleReceived(object? sender, RumbleEventArgs e)
			{
				_ = _owner.BroadcastRumbleAsync(_sessionId, e);
			}

            private bool CheckSession()
            {
                var stream = _streamingService.GetStreamAsync(_sessionId).Result;
                if (stream == null)
                {
                    _logger.LogWarning("流不存在，会话 {SessionId}", _sessionId);
                    return false;
                }

				AttachStream(stream);
                return true;
            }
        }

        private static byte NormalizeTrigger(float value)
        {
            var clamped = Math.Clamp(value, 0f, 1f);
            var scaled = (int)Math.Round(clamped * 255f);
            return (byte)Math.Clamp(scaled, 0, 255);
        }
    }
}

