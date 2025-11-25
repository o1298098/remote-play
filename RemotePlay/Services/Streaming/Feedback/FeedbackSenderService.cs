using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.Controller;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlay.Services.Streaming.Feedback
{
    /// <summary>
    /// Feedback 发送服务 - 负责持续发送 Feedback State 和 Feedback History
    /// 这是 Remote Play 连接的关键心跳机制
    /// </summary>
    public class FeedbackSenderService : IDisposable
    {
        #region Constants
        
        // ✅ 时间常量
        private const int FEEDBACK_STATE_TIMEOUT_MIN_MS = 8;      // 最小发送间隔
        private const int FEEDBACK_STATE_TIMEOUT_MAX_MS = 200;    // 最大发送间隔（超时心跳）
        private const int FEEDBACK_HISTORY_BUFFER_SIZE = 16;      // History 缓冲区大小
        
        #endregion

        #region Fields
        
        private readonly ILogger<FeedbackSenderService> _logger;
        private readonly Func<int, ushort, byte[], Task> _sendFeedbackFunc;  // 发送回调
        
        private CancellationTokenSource? _cts;
        private Task? _feedbackLoop;
        
        private Models.PlayStation.ControllerState _currentState;
        private Models.PlayStation.ControllerState _previousState;
        private readonly object _stateLock = new object();
        private bool _stateChanged = false;
        
        private ushort _feedbackStateSeqNum = 0;
        private ushort _feedbackHistorySeqNum = 0;
        
        private readonly Queue<FeedbackHistoryEvent> _historyBuffer = new Queue<FeedbackHistoryEvent>();
        
        private bool _isRunning = false;
        private readonly AsyncManualResetEvent _stateChangedEvent = new AsyncManualResetEvent(false);

        #endregion

        #region Constructor & Lifecycle
        
        /// <summary>
        /// 创建 FeedbackSender 服务
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="sendFeedbackFunc">发送 Feedback 的回调函数 (type, sequence, data)</param>
        public FeedbackSenderService(
            ILogger<FeedbackSenderService> logger,
            Func<int, ushort, byte[], Task> sendFeedbackFunc)
        {
            _logger = logger;
            _sendFeedbackFunc = sendFeedbackFunc;
            
            // 初始化为 idle 状态
            _currentState = Models.PlayStation.ControllerState.CreateIdle();
            _previousState = Models.PlayStation.ControllerState.CreateIdle();
        }
        
        /// <summary>
        /// 启动 Feedback 发送循环
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                _logger.LogWarning("FeedbackSender already running");
                return;
            }
            
            _cts = new CancellationTokenSource();
            _feedbackLoop = Task.Run(() => FeedbackLoopAsync(_cts.Token), _cts.Token);
            _isRunning = true;
            
            _logger.LogInformation("✅ FeedbackSender started - will send every {TimeoutMs}ms max", 
                FEEDBACK_STATE_TIMEOUT_MAX_MS);
        }
        
        /// <summary>
        /// 停止 Feedback 发送循环
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;
            
            _cts?.Cancel();
            
            if (_feedbackLoop != null)
            {
                try
                {
                    await _feedbackLoop;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
            
            _isRunning = false;
            _logger.LogInformation("FeedbackSender stopped");
        }
        
        public void Dispose()
        {
            // ✅ 关键修复：使用超时机制，避免 Dispose 阻塞太久
            try
            {
                var stopTask = StopAsync();
                var timeoutTask = Task.Delay(1000); // 最多等待 1 秒
                var completedTask = Task.WhenAny(stopTask, timeoutTask).GetAwaiter().GetResult();
                
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("⚠️ FeedbackSender StopAsync 超时（1秒），强制继续释放");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ FeedbackSender Dispose 异常，继续释放");
            }
            
            _cts?.Dispose();
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// 更新 Controller 状态（从外部调用）
        /// ✅ 关键：合并状态而不是完全替换，保留陀螺仪等其他状态
        /// </summary>
        public void UpdateControllerState(Models.PlayStation.ControllerState state)
        {
            lock (_stateLock)
            {
                // ✅ 合并状态：只更新摇杆值，保留其他状态（陀螺仪、按键等）
                // 这样即使只更新摇杆，也不会丢失其他状态
                bool changed = false;
                
                // 检查摇杆是否变化
                if (_currentState.LeftX != state.LeftX || 
                    _currentState.LeftY != state.LeftY ||
                    _currentState.RightX != state.RightX ||
                    _currentState.RightY != state.RightY)
                {
                    _currentState.LeftX = state.LeftX;
                    _currentState.LeftY = state.LeftY;
                    _currentState.RightX = state.RightX;
                    _currentState.RightY = state.RightY;
                    changed = true;
                }
                
                // 如果提供了其他状态，也更新（如按键、扳机等）
                // 但主要关注摇杆，因为这是最常见的更新场景
                if (state.Buttons != _currentState.Buttons)
                {
                    _currentState.Buttons = state.Buttons;
                    changed = true;
                }
                
                if (state.L2State != _currentState.L2State)
                {
                    _currentState.L2State = state.L2State;
                    changed = true;
                }
                
                if (state.R2State != _currentState.R2State)
                {
                    _currentState.R2State = state.R2State;
                    changed = true;
                }
                
                // 标记状态已改变
                if (changed)
                {
                    _stateChanged = true;
                    _stateChangedEvent.Set();

                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace(
                            "FeedbackSender UpdateControllerState: Left=({LeftXShort},{LeftYShort}) Right=({RightXShort},{RightYShort}) LeftNorm=({LeftX:F4},{LeftY:F4}) RightNorm=({RightX:F4},{RightY:F4})",
                            _currentState.LeftX,
                            _currentState.LeftY,
                            _currentState.RightX,
                            _currentState.RightY,
                            _currentState.LeftX / 32767f,
                            _currentState.LeftY / 32767f,
                            _currentState.RightX / 32767f,
                            _currentState.RightY / 32767f);
                    }
                }
            }
        }
        
        #endregion

        #region Feedback Loop (Main Thread)
        
        /// <summary>
        /// Feedback 发送主循环
        /// ✅ 关键：每 200ms 超时自动发送，即使状态不变
        /// </summary>
        private async Task FeedbackLoopAsync(CancellationToken ct)
        {
            _logger.LogInformation("🔄 FeedbackSender loop started");
            
            var nextTimeout = FEEDBACK_STATE_TIMEOUT_MAX_MS;
            int stateCount = 0;
            int historyCount = 0;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ✅ 等待状态变化或超时（最多 200ms）
                    var waitTask = _stateChangedEvent.WaitAsync(ct);
                    var delayTask = Task.Delay(nextTimeout, ct);
                    var completed = await Task.WhenAny(waitTask, delayTask);
                    if (completed == waitTask)
                    {
                        // 立即处理状态变化，下次回到默认 8ms 最小间隔
                        nextTimeout = FEEDBACK_STATE_TIMEOUT_MIN_MS;
                    }
                    else
                    {
                        nextTimeout = FEEDBACK_STATE_TIMEOUT_MAX_MS;
                    }
                    _stateChangedEvent.Reset();
                    
                    bool sendFeedbackState = false;
                    bool sendFeedbackHistory = false;
                    
                    Models.PlayStation.ControllerState currentStateCopy;
                    Models.PlayStation.ControllerState previousStateCopy;
                    
                    lock (_stateLock)
                    {
                        currentStateCopy = _currentState.Clone();
                        previousStateCopy = _previousState.Clone();
                        
                        if (_stateChanged)
                        {
                            _stateChanged = false;
                            
                            // ✅ 检查是否需要发送 Feedback State
                            // 只有摇杆/陀螺仪变化时才发送 State
                            if (!StatesEqualForFeedbackState(previousStateCopy, currentStateCopy))
                            {
                                sendFeedbackState = true;
                            }
                            
                            // ✅ 检查是否需要发送 Feedback History
                            // 按键/扳机变化时发送 History
                            if (!StatesEqualForFeedbackHistory(previousStateCopy, currentStateCopy))
                            {
                                sendFeedbackHistory = true;
                            }
                            nextTimeout = FEEDBACK_STATE_TIMEOUT_MIN_MS;
                        }
                        else
                        {
                            // ✅ 超时：即使状态不变，也发送 Feedback State（心跳）
                            sendFeedbackState = true;
                        }
                    }
                    
                    // 发送 Feedback State
                    if (sendFeedbackState)
                    {
                        await SendFeedbackStateAsync(currentStateCopy);
                        stateCount++;
                        
                        if (stateCount <= 3 || stateCount % 20 == 0)
                        {
                            _logger.LogDebug("📤 Feedback State #{Count} sent", stateCount);
                        }
                    }
                    
                    // 发送 Feedback History
                    if (sendFeedbackHistory)
                    {
                        await SendFeedbackHistoryAsync(previousStateCopy, currentStateCopy);
                        historyCount++;
                        
                        _logger.LogDebug("📤 Feedback History #{Count} sent (button change detected)", 
                            historyCount);
                    }
                    
                    // 更新 previous state
                    lock (_stateLock)
                    {
                        _previousState.CopyFrom(currentStateCopy);
                    }
                    
                    // ✅ 如果状态连续变化，会维持最小发送间隔
                    if (!sendFeedbackState && !sendFeedbackHistory)
                    {
                        nextTimeout = FEEDBACK_STATE_TIMEOUT_MAX_MS;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in FeedbackSender loop");
                    await Task.Delay(100, ct);  // 短暂延迟后继续
                }
            }
            
            _logger.LogInformation("FeedbackSender loop exited (sent {StateCount} states, {HistoryCount} histories)", 
                stateCount, historyCount);
        }
        
        #endregion

        #region Feedback State
        
        /// <summary>
        /// 发送 Feedback State（陀螺仪 + 摇杆）
        /// 格式：参考 feedback_state_format_v12()
        /// </summary>
        private async Task SendFeedbackStateAsync(Models.PlayStation.ControllerState state)
        {
            // ✅ PS5 格式：28 字节 (0x1c)
            // ✅ PS4 格式：25 字节 (0x19)
            // 这里使用 PS5 格式
            
            var data = new byte[28];
            int offset = 0;
            
            // [0x00] 固定字节
            data[offset++] = 0xa0;
            
            // [0x01-0x02] Gyro X (int16)
            WriteGyroValue(data, ref offset, state.GyroX);
            
            // [0x03-0x04] Gyro Y (int16)
            WriteGyroValue(data, ref offset, state.GyroY);
            
            // [0x05-0x06] Gyro Z (int16)
            WriteGyroValue(data, ref offset, state.GyroZ);
            
            // [0x07-0x08] Accel X (int16)
            WriteAccelValue(data, ref offset, state.AccelX);
            
            // [0x09-0x0a] Accel Y (int16)
            WriteAccelValue(data, ref offset, state.AccelY);
            
            // [0x0b-0x0c] Accel Z (int16)
            WriteAccelValue(data, ref offset, state.AccelZ);
            
            // [0x0d-0x10] Orientation Quaternion (压缩格式，4 字节)
            WriteOrientationQuaternion(data, ref offset, 
                state.OrientX, state.OrientY, state.OrientZ, state.OrientW);
            
            // [0x11-0x12] Left Stick X (int16, big-endian)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset), state.LeftX);
            offset += 2;
            
            // [0x13-0x14] Left Stick Y (int16, big-endian)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset), state.LeftY);
            offset += 2;
            
            // [0x15-0x16] Right Stick X (int16, big-endian)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset), state.RightX);
            offset += 2;
            
            // [0x17-0x18] Right Stick Y (int16, big-endian)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset), state.RightY);
            offset += 2;
            
            // [0x19-0x1b] PS5 额外字节（PS4 不需要）
            data[offset++] = 0x00;
            data[offset++] = 0x00;
            data[offset++] = 0x01;
            
            // ✅ 前3个包记录完整hex
            if (_feedbackStateSeqNum < 3)
            {
                var hex = BitConverter.ToString(data).Replace("-", "");
                _logger.LogInformation("📤 FeedbackStateData seq={Seq} len={Len} hex={Hex}",
                    _feedbackStateSeqNum, data.Length, hex);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "FeedbackSender SendFeedbackState seq={Seq} LeftShort=({LeftXShort},{LeftYShort}) RightShort=({RightXShort},{RightYShort}) LeftNorm=({LeftX:F4},{LeftY:F4}) RightNorm=({RightX:F4},{RightY:F4})",
                    _feedbackStateSeqNum,
                    state.LeftX,
                    state.LeftY,
                    state.RightX,
                    state.RightY,
                    state.LeftX / 32767f,
                    state.LeftY / 32767f,
                    state.RightX / 32767f,
                    state.RightY / 32767f);
            }
            
            // 发送 Feedback State（type=6）
            await _sendFeedbackFunc((int)FeedbackHeaderType.STATE, _feedbackStateSeqNum++, data);
        }
        
        /// <summary>
        /// 写入陀螺仪值（范围：-30.0 ~ 30.0 → 0x0000 ~ 0xffff）
        /// </summary>
        private void WriteGyroValue(byte[] buffer, ref int offset, float value)
        {
            const float GYRO_MIN = -30.0f;
            const float GYRO_MAX = 30.0f;
            
            float normalized = (value - GYRO_MIN) / (GYRO_MAX - GYRO_MIN);
            normalized = Math.Clamp(normalized, 0.0f, 1.0f);
            ushort encoded = (ushort)(normalized * 0xffff);
            
            // Little-endian
            buffer[offset++] = (byte)(encoded & 0xff);
            buffer[offset++] = (byte)(encoded >> 8);
        }
        
        /// <summary>
        /// 写入加速度值（范围：-5.0 ~ 5.0 → 0x0000 ~ 0xffff）
        /// </summary>
        private void WriteAccelValue(byte[] buffer, ref int offset, float value)
        {
            const float ACCEL_MIN = -5.0f;
            const float ACCEL_MAX = 5.0f;
            
            float normalized = (value - ACCEL_MIN) / (ACCEL_MAX - ACCEL_MIN);
            normalized = Math.Clamp(normalized, 0.0f, 1.0f);
            ushort encoded = (ushort)(normalized * 0xffff);
            
            // Little-endian
            buffer[offset++] = (byte)(encoded & 0xff);
            buffer[offset++] = (byte)(encoded >> 8);
        }
        
        /// <summary>
        /// 写入方向四元数（压缩为 4 字节）
        /// 参考 compress_quat()
        /// </summary>
        private void WriteOrientationQuaternion(byte[] buffer, ref int offset,
            float x, float y, float z, float w)
        {
            // 找到最大分量
            float[] q = { x, y, z, w };
            int largestIndex = 0;
            float largestAbs = Math.Abs(q[0]);
            
            for (int i = 1; i < 4; i++)
            {
                float abs = Math.Abs(q[i]);
                if (abs > largestAbs)
                {
                    largestAbs = abs;
                    largestIndex = i;
                }
            }
            
            // 构造压缩值
            uint compressed = (uint)((q[largestIndex] < 0.0 ? 1 : 0) | (largestIndex << 1));
            
            // 编码其他 3 个分量（每个 9 位）
            for (int i = 0; i < 3; i++)
            {
                int qi = i < largestIndex ? i : i + 1;
                float v = q[qi];
                
                // Clamp to [-√2/2, √2/2]
                const float SQRT2_2 = 0.70710678118f;
                v = Math.Clamp(v, -SQRT2_2, SQRT2_2);
                
                // Normalize to [0, 1]
                v += SQRT2_2;
                v /= (2.0f * SQRT2_2);
                
                // Encode to 9 bits
                uint encoded = (uint)(v * 0x1ff);
                compressed |= encoded << (3 + i * 9);
            }
            
            // Write as little-endian uint32
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), compressed);
            offset += 4;
        }
        
        #endregion

        #region Feedback History
        
        /// <summary>
        /// 发送 Feedback History（按键事件）
        /// </summary>
        private async Task SendFeedbackHistoryAsync(Models.PlayStation.ControllerState prev, Models.PlayStation.ControllerState current)
        {
            var events = new List<byte>();
            
            // ✅ 检查所有按键变化
            CheckButtonChanges(prev.Buttons, current.Buttons, events);
            
            // ✅ 检查 L2/R2 扳机变化
            if (prev.L2State != current.L2State)
            {
                AddButtonEvent(events, 0x86, current.L2State);  // L2
            }
            
            if (prev.R2State != current.R2State)
            {
                AddButtonEvent(events, 0x87, current.R2State);  // R2
            }
            
            // ✅ 检查触摸板变化（如果有实现）
            // TODO: 实现触摸板事件
            
            // 如果有事件，发送 Feedback History
            if (events.Count > 0)
            {
                await _sendFeedbackFunc((int)FeedbackHeaderType.HISTORY, 
                    _feedbackHistorySeqNum++, 
                    events.ToArray());
            }
        }
        
        /// <summary>
        /// 检查按键变化
        /// </summary>
        private void CheckButtonChanges(ulong prevButtons, ulong currentButtons, List<byte> events)
        {
            // 遍历所有 32 个可能的按键
            for (int i = 0; i < 32; i++)
            {
                ulong buttonMask = 1UL << i;
                bool prevPressed = (prevButtons & buttonMask) != 0;
                bool currPressed = (currentButtons & buttonMask) != 0;
                
                if (prevPressed != currPressed)
                {
                    byte buttonCode = GetButtonCode(buttonMask);
                    if (buttonCode != 0)
                    {
                        AddButtonEvent(events, buttonCode, currPressed ? (byte)0xff : (byte)0x00);
                    }
                }
            }
        }
        
        /// <summary>
        /// 添加按键事件到缓冲区
        /// 格式：[0x80] [button_code] [state]（部分按键有 state，部分没有）
        /// </summary>
        private void AddButtonEvent(List<byte> events, byte buttonCode, byte state)
        {
            events.Add(0x80);
            
            // ✅ 部分按键需要特殊处理（按下/释放用不同的 code）
            switch (buttonCode)
            {
                case 0x8c: // OPTIONS
                    events.Add((byte)(state != 0 ? 0xac : 0x8c));
                    break;
                case 0x8d: // SHARE
                    events.Add((byte)(state != 0 ? 0xad : 0x8d));
                    break;
                case 0x8e: // PS
                    events.Add((byte)(state != 0 ? 0xae : 0x8e));
                    break;
                case 0x8f: // L3
                    events.Add((byte)(state != 0 ? 0xaf : 0x8f));
                    break;
                case 0x90: // R3
                    events.Add((byte)(state != 0 ? 0xb0 : 0x90));
                    break;
                case 0x91: // TOUCHPAD
                    events.Add((byte)(state != 0 ? 0xb1 : 0x91));
                    break;
                default:
                    // 其他按键需要 state 字节
                    events.Add(buttonCode);
                    events.Add(state);
                    break;
            }
        }
        
        /// <summary>
        /// 获取按键对应的 button code
        /// </summary>
        private byte GetButtonCode(ulong buttonMask)
        {
            // ✅ 按照官方按钮编码进行映射
            return buttonMask switch
            {
                0x0001 => 0x88, // CROSS
                0x0002 => 0x89, // CIRCLE (MOON)
                0x0004 => 0x8a, // SQUARE (BOX)
                0x0008 => 0x8b, // TRIANGLE (PYRAMID)
                0x0010 => 0x82, // DPAD_LEFT
                0x0020 => 0x80, // DPAD_UP
                0x0040 => 0x83, // DPAD_RIGHT
                0x0080 => 0x81, // DPAD_DOWN
                0x0100 => 0x84, // L1
                0x0200 => 0x85, // R1
                0x1000 => 0x8c, // OPTIONS
                0x2000 => 0x8d, // SHARE
                0x4000 => 0x8f, // L3
                0x8000 => 0x90, // R3
                0x10000 => 0x8e, // PS
                0x100000 => 0x91, // TOUCHPAD
                _ => 0 // 未知按键
            };
        }
        
        #endregion

        #region State Comparison
        
        /// <summary>
        /// 检查两个状态在 Feedback State 方面是否相同
        /// （只比较摇杆和陀螺仪，不比较按键）
        /// </summary>
        private bool StatesEqualForFeedbackState(Models.PlayStation.ControllerState a, Models.PlayStation.ControllerState b)
        {
            // 摇杆
            if (a.LeftX != b.LeftX || a.LeftY != b.LeftY) return false;
            if (a.RightX != b.RightX || a.RightY != b.RightY) return false;
            
            // 陀螺仪（允许微小误差）
            const float epsilon = 0.0001f;
            if (Math.Abs(a.GyroX - b.GyroX) > epsilon) return false;
            if (Math.Abs(a.GyroY - b.GyroY) > epsilon) return false;
            if (Math.Abs(a.GyroZ - b.GyroZ) > epsilon) return false;
            
            // 加速度
            if (Math.Abs(a.AccelX - b.AccelX) > epsilon) return false;
            if (Math.Abs(a.AccelY - b.AccelY) > epsilon) return false;
            if (Math.Abs(a.AccelZ - b.AccelZ) > epsilon) return false;
            
            // 方向
            if (Math.Abs(a.OrientX - b.OrientX) > epsilon) return false;
            if (Math.Abs(a.OrientY - b.OrientY) > epsilon) return false;
            if (Math.Abs(a.OrientZ - b.OrientZ) > epsilon) return false;
            if (Math.Abs(a.OrientW - b.OrientW) > epsilon) return false;
            
            return true;
        }
        
        /// <summary>
        /// 检查两个状态在 Feedback History 方面是否相同
        /// （只比较按键和扳机）
        /// </summary>
        private bool StatesEqualForFeedbackHistory(Models.PlayStation.ControllerState a, Models.PlayStation.ControllerState b)
        {
            // 按键
            if (a.Buttons != b.Buttons) return false;
            
            // 扳机
            if (a.L2State != b.L2State) return false;
            if (a.R2State != b.R2State) return false;
            
            // TODO: 触摸板
            
            return true;
        }
        
        #endregion

        private sealed class AsyncManualResetEvent
        {
            private volatile TaskCompletionSource<bool> _tcs;

            public AsyncManualResetEvent(bool initialState)
            {
                _tcs = CreateTcs(initialState);
            }

            private static TaskCompletionSource<bool> CreateTcs(bool set)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (set)
                {
                    tcs.TrySetResult(true);
                }
                return tcs;
            }

            public Task WaitAsync(CancellationToken ct)
            {
                return _tcs.Task.WaitAsync(ct);
            }

            public void Set()
            {
                _tcs.TrySetResult(true);
            }

            public void Reset()
            {
                while (true)
                {
                    var current = _tcs;
                    if (!current.Task.IsCompleted)
                    {
                        return;
                    }
                    var newTcs = CreateTcs(false);
                    if (Interlocked.CompareExchange(ref _tcs, newTcs, current) == current)
                    {
                        return;
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Feedback History 事件
    /// </summary>
    internal struct FeedbackHistoryEvent
    {
        public byte[]? Buffer;
        public int Length;
        
        public FeedbackHistoryEvent()
        {
            Buffer = null;
            Length = 0;
        }
    }
    
}

