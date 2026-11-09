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
                _logger.LogWarning("Controller already connected to session {SessionId}; disconnect first", sessionId);
                return false;
            }

            var session = await _streamingService.GetSessionAsync(sessionId);
            if (session == null)
            {
                _logger.LogError("Session {SessionId} does not exist", sessionId);
                return false;
            }

			var controller = new ControllerInstance(sessionId, this, _logger, _streamingService);
			_controllers[sessionId] = controller;

			var stream = await _streamingService.GetStreamAsync(sessionId);
			if (stream != null)
			{
				controller.AttachStream(stream);
			}
            
            _logger.LogInformation("Controller connected to session {SessionId}", sessionId);
            return true;
        }

        public Task DisconnectAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (_controllers.TryRemove(sessionId, out var controller))
            {
                controller.Stop();
                _logger.LogInformation("Controller disconnected from session {SessionId}", sessionId);
            }
            return Task.CompletedTask;
        }

        public Task<bool> StartAsync(Guid sessionId, CancellationToken ct = default)
        {
            if (!_controllers.TryGetValue(sessionId, out var controller))
            {
                _logger.LogWarning("No controller connected for session {SessionId}", sessionId);
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
                _logger.LogWarning("No controller connected for session {SessionId}", sessionId);
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
                _logger.LogWarning("No controller connected for session {SessionId}", sessionId);
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
                _logger.LogWarning("No controller connected for session {SessionId}", sessionId);
                return Task.CompletedTask;
            }

            controller.SetTriggers(l2, r2);
            return Task.CompletedTask;
        }

        public Task SetMotionAsync(Guid sessionId, float gyroX, float gyroY, float gyroZ, float accelX, float accelY, float accelZ,
            float orientX, float orientY, float orientZ, float orientW)
        {
            if (_controllers.TryGetValue(sessionId, out var controller))
            {
                controller.SetMotion(gyroX, gyroY, gyroZ, accelX, accelY, accelZ, orientX, orientY, orientZ, orientW);
            }
            return Task.CompletedTask;
        }

        public Task SetTouchpadAsync(Guid sessionId, IReadOnlyList<TouchpadSlotInput> slots)
        {
            return _controllers.TryGetValue(sessionId, out var controller)
                ? controller.SetTouchpadAsync(slots)
                : Task.CompletedTask;
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

		internal Task BroadcastTriggerEffectsAsync(Guid sessionId, TriggerEffectsEventArgs effects) =>
			SendToControllerGroupAsync(sessionId, "ControllerTriggerEffects", ToTriggerEffectsPayload(sessionId, effects));

		internal Task BroadcastLightbarAsync(Guid sessionId, LightbarEventArgs lightbar) =>
			SendToControllerGroupAsync(sessionId, "ControllerLightbar", ToLightbarPayload(sessionId, lightbar));

		public async Task<(object? TriggerEffects, object? Lightbar)> GetDualSenseStateAsync(Guid sessionId)
		{
			var stream = await _streamingService.GetStreamAsync(sessionId);
			if (stream == null)
			{
				return (null, null);
			}

			var effects = stream.LastTriggerEffects;
			var lightbar = stream.LastLightbar;
			return (
				effects != null ? ToTriggerEffectsPayload(sessionId, effects) : null,
				lightbar != null ? ToLightbarPayload(sessionId, lightbar) : null);
		}

		// byte[] 会被序列化成 base64，前端需要数字数组
		private static object ToTriggerEffectsPayload(Guid sessionId, TriggerEffectsEventArgs e) => new
		{
			sessionId,
			typeLeft = e.TypeLeft,
			left = e.Left.Select(b => (int)b).ToArray(),
			typeRight = e.TypeRight,
			right = e.Right.Select(b => (int)b).ToArray(),
			intensity = e.Intensity
		};

		private static object ToLightbarPayload(Guid sessionId, LightbarEventArgs e) => new
		{
			sessionId,
			red = e.Red,
			green = e.Green,
			blue = e.Blue,
			playerIndex = e.PlayerIndex
		};

		internal async Task SendToControllerGroupAsync(Guid sessionId, string method, object payload)
		{
			try
			{
				await _controllerHubContext.Clients
					.Group(GetControllerGroupName(sessionId))
					.SendAsync(method, payload);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to broadcast {Method} for session {SessionId}", method, sessionId);
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

            private ControllerState _lastState = new();
            private ControllerState _stickState = new();
            private readonly object _inputLock = new();
            // L2/R2 可能同时来自模拟扳机（SetTriggers）和数字按键（键盘/虚拟手柄），按下的数字键优先
            private byte _analogL2;
            private byte _analogR2;
            private bool _digitalL2;
            private bool _digitalR2;
            private const int TOUCHPAD_MAX_X = 1919;
            private const int TOUCHPAD_MAX_Y = 1079;
            private readonly int[] _touchSlotIds = { -1, -1 };
            private readonly int[] _touchSlotContacts = { -1, -1 };
            private int _touchIdNext;
            
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
						_attachedStream.TriggerEffectsReceived -= OnTriggerEffectsReceived;
						_attachedStream.LightbarChanged -= OnLightbarChanged;
						_attachedStream.MotionResetRequested -= OnMotionResetRequested;
					}

					_attachedStream = stream;
					_attachedStream.RumbleReceived += OnRumbleReceived;
					_attachedStream.TriggerEffectsReceived += OnTriggerEffectsReceived;
					_attachedStream.LightbarChanged += OnLightbarChanged;
					_attachedStream.MotionResetRequested += OnMotionResetRequested;
				}
			}

			private void DetachStream()
			{
				lock (_streamSubscriptionLock)
				{
					if (_attachedStream != null)
					{
						_attachedStream.RumbleReceived -= OnRumbleReceived;
						_attachedStream.TriggerEffectsReceived -= OnTriggerEffectsReceived;
						_attachedStream.LightbarChanged -= OnLightbarChanged;
						_attachedStream.MotionResetRequested -= OnMotionResetRequested;
						_attachedStream = null;
					}
				}
			}

            public void Start()
            {
                if (_workerTask != null)
                {
                    _logger.LogWarning("Controller is already running");
                    return;
                }

                _cts = new CancellationTokenSource();
                _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token), _cts.Token);
                _logger.LogDebug("Controller worker started for session {SessionId}", _sessionId);
            }

            public void Stop()
            {
                _cts?.Cancel();
				DetachStream();
                _workerTask?.Wait(TimeSpan.FromSeconds(1));
                _cts?.Dispose();
                _cts = null;
                _workerTask = null;
                _logger.LogInformation("Controller stopped for session {SessionId}", _sessionId);
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
                    _logger.LogError(ex, "Controller worker loop error");
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
                    _logger.LogWarning("Cannot send button event: no stream for session {SessionId}", _sessionId);
                    return;
                }

                _logger.LogDebug("Sending button event: button={Button}, action={Action}, delay={DelayMs}ms, session={SessionId}", 
                    button, action, delayMs, _sessionId);

                // 按键并入控制器状态，由 FeedbackSender 统一生成带序号的 History 事件，
                // 保证按键和扳机共用一条序号流
                SetButton(button, pressed: action != IControllerService.ButtonAction.RELEASE);
                await PushStateAsync();

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
            bool changed;
            lock (_inputLock)
            {
                if (l2.HasValue)
                {
                    _analogL2 = NormalizeTrigger(l2.Value);
                }

                if (r2.HasValue)
                {
                    _analogR2 = NormalizeTrigger(r2.Value);
                }

                changed = RefreshTriggers_NoLock();
            }

            if (changed)
            {
                _shouldSend.Release();
            }
        }

        private void SetButton(FeedbackEvent.ButtonType button, bool pressed)
        {
            lock (_inputLock)
            {
                switch (button)
                {
                    case FeedbackEvent.ButtonType.L2:
                        _digitalL2 = pressed;
                        RefreshTriggers_NoLock();
                        return;
                    case FeedbackEvent.ButtonType.R2:
                        _digitalR2 = pressed;
                        RefreshTriggers_NoLock();
                        return;
                }

                var mask = GetButtonMask(button);
                if (mask == 0)
                {
                    return;
                }

                _stickState.Buttons = pressed
                    ? _stickState.Buttons | mask
                    : _stickState.Buttons & ~mask;
            }
        }

        public void SetMotion(float gyroX, float gyroY, float gyroZ, float accelX, float accelY, float accelZ,
            float orientX, float orientY, float orientZ, float orientW)
        {
            lock (_inputLock)
            {
                _stickState.GyroX = gyroX;
                _stickState.GyroY = gyroY;
                _stickState.GyroZ = gyroZ;
                _stickState.AccelX = accelX;
                _stickState.AccelY = accelY;
                _stickState.AccelZ = accelZ;
                _stickState.OrientX = orientX;
                _stickState.OrientY = orientY;
                _stickState.OrientZ = orientZ;
                _stickState.OrientW = orientW;
                _stickState.HasMotion = true;
            }

            _shouldSend.Release();
        }

        /// <summary>
        /// 更新触摸板：每个手指槽位按下时分配新的触摸 id（同 chiaki），移动时更新坐标，抬起时释放
        /// </summary>
        public async Task SetTouchpadAsync(IReadOnlyList<TouchpadSlotInput> slots)
        {
            bool changed = false;
            lock (_inputLock)
            {
                var touches = (Models.PlayStation.ControllerTouch[])_stickState.Touches.Clone();
                for (int slot = 0; slot < _touchSlotIds.Length; slot++)
                {
                    var input = slot < slots.Count ? slots[slot] : null;
                    int index = _touchSlotIds[slot] >= 0 ? Array.FindIndex(touches, t => t.Id == _touchSlotIds[slot]) : -1;

                    bool restarted = input != null && input.Down && index >= 0 && input.Contact != _touchSlotContacts[slot];
                    if (index >= 0 && (input == null || !input.Down || restarted))
                    {
                        touches[index] = new Models.PlayStation.ControllerTouch { Id = -1 };
                        _touchSlotIds[slot] = -1;
                        index = -1;
                    }

                    if (input == null || !input.Down)
                    {
                        continue;
                    }

                    var x = (ushort)Math.Clamp(input.X, 0, TOUCHPAD_MAX_X);
                    var y = (ushort)Math.Clamp(input.Y, 0, TOUCHPAD_MAX_Y);
                    if (index < 0)
                    {
                        index = Array.FindIndex(touches, t => t.Id < 0);
                        if (index < 0)
                        {
                            continue;
                        }
                        _touchSlotIds[slot] = _touchIdNext;
                        _touchSlotContacts[slot] = input.Contact;
                        _touchIdNext = (_touchIdNext + 1) & 0x7f;
                    }

                    touches[index] = new Models.PlayStation.ControllerTouch { Id = _touchSlotIds[slot], X = x, Y = y };
                }

                if (!touches.AsSpan().SequenceEqual(_stickState.Touches))
                {
                    _stickState.Touches = touches;
                    changed = true;
                }
            }

            if (changed)
            {
                await PushStateAsync();
            }
        }

        private bool RefreshTriggers_NoLock()
        {
            byte l2 = _digitalL2 ? (byte)0xff : _analogL2;
            byte r2 = _digitalR2 ? (byte)0xff : _analogR2;
            bool changed = _stickState.L2State != l2 || _stickState.R2State != r2;
            _stickState.L2State = l2;
            _stickState.R2State = r2;
            return changed;
        }

        private async Task PushStateAsync()
        {
            var stream = await _streamingService.GetStreamAsync(_sessionId);
            if (stream == null)
            {
                _logger.LogWarning("Cannot send controller state: stream not found, session={SessionId}", _sessionId);
                return;
            }

            AttachStream(stream);
            lock (_inputLock)
            {
                stream.UpdateControllerState(_stickState);
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
                    lock (_inputLock)
                    {
                        stream.UpdateControllerState(_stickState);
                    }
                }
            }

            /// <summary>
            /// ButtonType 对应的控制器状态按键位（与 FeedbackSenderService.GetButtonCode 互逆）
            /// </summary>
            private static ulong GetButtonMask(FeedbackEvent.ButtonType button) => button switch
            {
                FeedbackEvent.ButtonType.CROSS => 0x0001,
                FeedbackEvent.ButtonType.CIRCLE => 0x0002,
                FeedbackEvent.ButtonType.SQUARE => 0x0004,
                FeedbackEvent.ButtonType.TRIANGLE => 0x0008,
                FeedbackEvent.ButtonType.LEFT => 0x0010,
                FeedbackEvent.ButtonType.UP => 0x0020,
                FeedbackEvent.ButtonType.RIGHT => 0x0040,
                FeedbackEvent.ButtonType.DOWN => 0x0080,
                FeedbackEvent.ButtonType.L1 => 0x0100,
                FeedbackEvent.ButtonType.R1 => 0x0200,
                FeedbackEvent.ButtonType.OPTIONS => 0x1000,
                FeedbackEvent.ButtonType.SHARE => 0x2000,
                FeedbackEvent.ButtonType.L3 => 0x4000,
                FeedbackEvent.ButtonType.R3 => 0x8000,
                FeedbackEvent.ButtonType.PS => 0x10000,
                FeedbackEvent.ButtonType.TOUCHPAD => 0x100000,
                _ => 0,
            };

			private void OnRumbleReceived(object? sender, RumbleEventArgs e)
			{
				_ = _owner.BroadcastRumbleAsync(_sessionId, e);
			}

			private void OnTriggerEffectsReceived(object? sender, TriggerEffectsEventArgs e)
			{
				_ = _owner.BroadcastTriggerEffectsAsync(_sessionId, e);
			}

			private void OnLightbarChanged(object? sender, LightbarEventArgs e)
			{
				_ = _owner.BroadcastLightbarAsync(_sessionId, e);
			}

			private void OnMotionResetRequested(object? sender, EventArgs e)
			{
				_ = _owner.SendToControllerGroupAsync(_sessionId, "ControllerMotionReset", new { sessionId = _sessionId });
			}

            private bool CheckSession()
            {
                var stream = _streamingService.GetStreamAsync(_sessionId).Result;
                if (stream == null)
                {
                    _logger.LogWarning("Stream not found for session {SessionId}", _sessionId);
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

