using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.Controller;
using System.Buffers.Binary;
using System.Diagnostics;
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
        // 每个 History 包重复携带的最近事件数。L2/R2 每次数值变化都是一个事件，
        // 只带 4 个时 L1/R1 的松开事件可能一两个包就被挤出窗口
        private const int FEEDBACK_HISTORY_RESEND_EVENT_COUNT = 8;
        private const int FEEDBACK_HISTORY_PACKET_QUEUE_SIZE = 64;
        // History 包走 UDP 且无确认。最后一个包（通常是松开按键）丢了又没有后续输入时，
        // 主机会一直认为按键按着，所以在这些时间点（相对首次发送，毫秒）用同一序号补发
        private static readonly int[] FEEDBACK_HISTORY_TAIL_MS = { 20, 50, 100, 200 };
        
        #endregion

        #region Fields
        
        private readonly ILogger<FeedbackSenderService> _logger;
        private readonly Func<int, ushort, byte, byte[], Task> _sendFeedbackFunc;  // 发送回调 (type, sequence, eventCount, data)
        
        private CancellationTokenSource? _cts;
        private Task? _feedbackLoop;
        
        private Models.PlayStation.ControllerState _currentState;
        private Models.PlayStation.ControllerState _previousState;
        private readonly Models.PlayStation.ControllerState _historyPrevState;
        private readonly object _stateLock = new object();
        private bool _stateChanged = false;
        
        private ushort _feedbackStateSeqNum = 0;
        
        // 每个 History 事件占一个序号，包的序号是其中最新事件的序号。
        // 主机记住已应用的最后序号，只取序号前进了几个就取几个事件，因此重复包不会被重复应用
        private ushort _historyEventSeqNum = 0;
        private readonly List<byte[]> _historyEvents = new();  // 最新的在前
        private readonly Queue<HistoryPacket> _pendingHistoryPackets = new();
        private HistoryPacket? _lastHistoryPacket;
        private long _lastHistorySentMs;
        private int _historyTailResends = FEEDBACK_HISTORY_TAIL_MS.Length;
        
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
            Func<int, ushort, byte, byte[], Task> sendFeedbackFunc)
        {
            _logger = logger;
            _sendFeedbackFunc = sendFeedbackFunc;
            
            // 初始化为 idle 状态
            _currentState = Models.PlayStation.ControllerState.CreateIdle();
            _previousState = Models.PlayStation.ControllerState.CreateIdle();
            _historyPrevState = Models.PlayStation.ControllerState.CreateIdle();
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
                    _logger.LogWarning("⚠️ FeedbackSender StopAsync timed out (1s), continuing dispose");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ FeedbackSender dispose error, continuing");
            }
            
            _cts?.Dispose();
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// 更新 Controller 状态（从外部调用）
        /// ✅ 关键：合并状态而不是完全替换，保留陀螺仪等其他状态
        /// </summary>
        /// <param name="includeMotion">state 是否带有真实体感数据；否则保留当前（静止）姿态</param>
        public void UpdateControllerState(Models.PlayStation.ControllerState state, bool includeMotion = false)
        {
            lock (_stateLock)
            {
                // ✅ 合并状态：只更新摇杆值，保留其他状态（陀螺仪、按键等）
                // 这样即使只更新摇杆，也不会丢失其他状态
                bool changed = false;

                if (includeMotion &&
                    (_currentState.GyroX != state.GyroX || _currentState.GyroY != state.GyroY || _currentState.GyroZ != state.GyroZ ||
                     _currentState.AccelX != state.AccelX || _currentState.AccelY != state.AccelY || _currentState.AccelZ != state.AccelZ ||
                     _currentState.OrientX != state.OrientX || _currentState.OrientY != state.OrientY ||
                     _currentState.OrientZ != state.OrientZ || _currentState.OrientW != state.OrientW))
                {
                    _currentState.GyroX = state.GyroX;
                    _currentState.GyroY = state.GyroY;
                    _currentState.GyroZ = state.GyroZ;
                    _currentState.AccelX = state.AccelX;
                    _currentState.AccelY = state.AccelY;
                    _currentState.AccelZ = state.AccelZ;
                    _currentState.OrientX = state.OrientX;
                    _currentState.OrientY = state.OrientY;
                    _currentState.OrientZ = state.OrientZ;
                    _currentState.OrientW = state.OrientW;
                    changed = true;
                }
                
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
                
                // 按键、扳机：立即记录为 History 事件，避免两次发送之间的快速按下/松开丢失
                bool historyChanged = false;
                if (state.Buttons != _currentState.Buttons)
                {
                    _currentState.Buttons = state.Buttons;
                    historyChanged = true;
                }
                
                if (state.L2State != _currentState.L2State)
                {
                    _currentState.L2State = state.L2State;
                    historyChanged = true;
                }
                
                if (state.R2State != _currentState.R2State)
                {
                    _currentState.R2State = state.R2State;
                    historyChanged = true;
                }

                if (!state.Touches.AsSpan().SequenceEqual(_currentState.Touches))
                {
                    _currentState.Touches = (Models.PlayStation.ControllerTouch[])state.Touches.Clone();
                    historyChanged = true;
                }

                if (historyChanged)
                {
                    RecordHistory(_historyPrevState, _currentState);
                    _historyPrevState.CopyFrom(_currentState);
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
            
            var clock = Stopwatch.StartNew();
            long lastStateSentMs = 0;
            int stateCount = 0;
            int historyCount = 0;
            
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ✅ 等待状态变化、State 心跳（200ms）或下一次 History 尾部补发
                    long nowMs = clock.ElapsedMilliseconds;
                    long timeoutMs = Math.Max(0, FEEDBACK_STATE_TIMEOUT_MAX_MS - (nowMs - lastStateSentMs));
                    lock (_stateLock)
                    {
                        if (_stateChanged || _pendingHistoryPackets.Count > 0)
                        {
                            timeoutMs = 0;
                        }
                        else if (_lastHistoryPacket != null && _historyTailResends < FEEDBACK_HISTORY_TAIL_MS.Length)
                        {
                            long dueMs = _lastHistorySentMs + FEEDBACK_HISTORY_TAIL_MS[_historyTailResends];
                            timeoutMs = Math.Min(timeoutMs, Math.Max(0, dueMs - nowMs));
                        }
                    }

                    if (timeoutMs > 0)
                    {
                        await Task.WhenAny(_stateChangedEvent.WaitAsync(ct), Task.Delay((int)timeoutMs, ct));
                        ct.ThrowIfCancellationRequested();
                    }
                    _stateChangedEvent.Reset();
                    
                    nowMs = clock.ElapsedMilliseconds;
                    bool sendFeedbackState = nowMs - lastStateSentMs >= FEEDBACK_STATE_TIMEOUT_MAX_MS;
                    HistoryPacket? historyPacket = null;
                    bool isTailResend = false;
                    Models.PlayStation.ControllerState currentStateCopy;
                    
                    lock (_stateLock)
                    {
                        currentStateCopy = _currentState.Clone();
                        
                        if (_stateChanged)
                        {
                            _stateChanged = false;
                            
                            // ✅ 只有摇杆/陀螺仪变化时才发送 State（按键/扳机走 History）
                            if (!StatesEqualForFeedbackState(_previousState, currentStateCopy))
                            {
                                sendFeedbackState = true;
                            }
                        }

                        if (_pendingHistoryPackets.Count > 0)
                        {
                            historyPacket = _pendingHistoryPackets.Dequeue();
                            _lastHistoryPacket = historyPacket;
                            _lastHistorySentMs = nowMs;
                            _historyTailResends = 0;
                        }
                        else if (_lastHistoryPacket != null
                            && _historyTailResends < FEEDBACK_HISTORY_TAIL_MS.Length
                            && nowMs >= _lastHistorySentMs + FEEDBACK_HISTORY_TAIL_MS[_historyTailResends])
                        {
                            historyPacket = _lastHistoryPacket;
                            _historyTailResends++;
                            isTailResend = true;
                        }
                    }
                    
                    // 发送 Feedback State
                    if (sendFeedbackState)
                    {
                        await SendFeedbackStateAsync(currentStateCopy);
                        lastStateSentMs = clock.ElapsedMilliseconds;
                        stateCount++;

                        lock (_stateLock)
                        {
                            _previousState.CopyFrom(currentStateCopy);
                        }
                        
                        if (stateCount <= 3 || stateCount % 20 == 0)
                        {
                            _logger.LogDebug("📤 Feedback State #{Count} sent", stateCount);
                        }
                    }
                    
                    // 发送 Feedback History
                    if (historyPacket != null)
                    {
                        await _sendFeedbackFunc((int)FeedbackHeaderType.HISTORY,
                            historyPacket.SeqNum,
                            historyPacket.EventCount,
                            historyPacket.Payload);

                        if (!isTailResend)
                        {
                            historyCount++;
                            _logger.LogDebug("📤 Feedback History #{Count} sent: seq={Seq}, events={Events}",
                                historyCount, historyPacket.SeqNum, historyPacket.EventCount);
                        }
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
            await _sendFeedbackFunc((int)FeedbackHeaderType.STATE, _feedbackStateSeqNum++, 1, data);
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
        /// 把按键/触摸/扳机变化记录为 History 事件（调用方需持有 _stateLock）
        /// 顺序：先松开、再触摸、再按下、最后 L2/R2。触摸板按键和手指同时松开时，
        /// 不能在按键还按着时先抬起手指
        /// </summary>
        private void RecordHistory(Models.PlayStation.ControllerState prev, Models.PlayStation.ControllerState current)
        {
            RecordButtonEvents(prev.Buttons, current.Buttons, pressed: false);

            for (int i = 0; i < Models.PlayStation.ControllerTouch.MaxTouches; i++)
            {
                var before = prev.Touches[i];
                var now = current.Touches[i];
                if (before.Id != now.Id && before.Id >= 0)
                {
                    PushHistoryEvent(CreateTouchEvent(down: false, before));
                }
                else if (now.Id >= 0 && (before.Id != now.Id || before.X != now.X || before.Y != now.Y))
                {
                    PushHistoryEvent(CreateTouchEvent(down: true, now));
                }
            }

            RecordButtonEvents(prev.Buttons, current.Buttons, pressed: true);

            if (prev.L2State != current.L2State)
            {
                PushHistoryEvent(CreateButtonEvent(0x86, current.L2State));  // L2
            }
            
            if (prev.R2State != current.R2State)
            {
                PushHistoryEvent(CreateButtonEvent(0x87, current.R2State));  // R2
            }
            
            // TODO: 实现触摸板事件
        }
        
        private void RecordButtonEvents(ulong prevButtons, ulong currentButtons, bool pressed)
        {
            for (int i = 0; i < 32; i++)
            {
                ulong buttonMask = 1UL << i;
                bool prevPressed = (prevButtons & buttonMask) != 0;
                bool currPressed = (currentButtons & buttonMask) != 0;
                
                if (prevPressed == currPressed || currPressed != pressed)
                {
                    continue;
                }

                byte buttonCode = GetButtonCode(buttonMask);
                if (buttonCode != 0)
                {
                    PushHistoryEvent(CreateButtonEvent(buttonCode, currPressed ? (byte)0xff : (byte)0x00));
                }
            }
        }

        /// <summary>
        /// 新事件放到缓冲区最前，并立即生成一个包含缓冲区全部事件的 History 包
        /// </summary>
        private void PushHistoryEvent(byte[] historyEvent)
        {
            _historyEvents.Insert(0, historyEvent);
            if (_historyEvents.Count > FEEDBACK_HISTORY_BUFFER_SIZE)
            {
                _historyEvents.RemoveAt(_historyEvents.Count - 1);
            }

            _historyEventSeqNum++;

            var payload = new byte[_historyEvents.Sum(e => e.Length)];
            int offset = 0;
            foreach (var e in _historyEvents)
            {
                System.Buffer.BlockCopy(e, 0, payload, offset, e.Length);
                offset += e.Length;
            }

            if (_pendingHistoryPackets.Count >= FEEDBACK_HISTORY_PACKET_QUEUE_SIZE)
            {
                // 丢掉最旧的包不影响编号：下一个包的序号仍覆盖上次发出后的所有事件
                _pendingHistoryPackets.Dequeue();
                _logger.LogWarning("Feedback history packet queue overflow");
            }
            _pendingHistoryPackets.Enqueue(new HistoryPacket(
                (ushort)(_historyEventSeqNum - 1),
                (byte)_historyEvents.Count,
                payload));

            if (_historyEvents.Count > FEEDBACK_HISTORY_RESEND_EVENT_COUNT)
            {
                _historyEvents.RemoveRange(FEEDBACK_HISTORY_RESEND_EVENT_COUNT,
                    _historyEvents.Count - FEEDBACK_HISTORY_RESEND_EVENT_COUNT);
            }

            _stateChangedEvent.Set();
        }
        
        /// <summary>
        /// 生成触摸事件：[0xd0 按下 / 0xc0 抬起] [触摸 id] [x 高 8 位] [x 低 4 位 | y 高 4 位] [y 低 8 位]
        /// </summary>
        private static byte[] CreateTouchEvent(bool down, Models.PlayStation.ControllerTouch touch)
        {
            return new byte[]
            {
                (byte)(down ? 0xd0 : 0xc0),
                (byte)(touch.Id & 0x7f),
                (byte)(touch.X >> 4),
                (byte)(((touch.X & 0xf) << 4) | (touch.Y >> 8)),
                (byte)touch.Y
            };
        }

        /// <summary>
        /// 生成单个按键事件
        /// 格式：[0x80] [button_code] [state]（部分按键按下/松开用不同 code 且没有 state 字节）
        /// </summary>
        private static byte[] CreateButtonEvent(byte buttonCode, byte state)
        {
            return buttonCode switch
            {
                0x8c => new byte[] { 0x80, (byte)(state != 0 ? 0xac : 0x8c) }, // OPTIONS
                0x8d => new byte[] { 0x80, (byte)(state != 0 ? 0xad : 0x8d) }, // SHARE
                0x8e => new byte[] { 0x80, (byte)(state != 0 ? 0xae : 0x8e) }, // PS
                0x8f => new byte[] { 0x80, (byte)(state != 0 ? 0xaf : 0x8f) }, // L3
                0x90 => new byte[] { 0x80, (byte)(state != 0 ? 0xb0 : 0x90) }, // R3
                0x91 => new byte[] { 0x80, (byte)(state != 0 ? 0xb1 : 0x91) }, // TOUCHPAD
                _ => new byte[] { 0x80, buttonCode, state },
            };
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
        
        #endregion

        private sealed record HistoryPacket(ushort SeqNum, byte EventCount, byte[] Payload);

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
}

