using Microsoft.Extensions.Logging;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlay.Services.Streaming.Emergency
{
    /// <summary>
    /// Emergency 恢复服务（参考 chiaki-ng 的 stream_connection 状态机）
    /// 
    /// 功能：
    /// 1. 检测长时间卡顿/失败
    /// 2. 触发 emergency 恢复流程
    /// 3. 重建 Takion 连接
    /// 4. 重新初始化流状态
    /// </summary>
    public class EmergencyRecoveryService : IDisposable
    {
        #region Constants

        // ✅ 恢复阈值（参考 chiaki-ng）
        // ✅ 降低阈值以更快响应：从5次降低到3次，更快触发恢复
        private const int SEVERE_FAILURE_THRESHOLD = 3; // 连续严重失败次数
        // ✅ 缩短长时间卡顿阈值：从10秒降低到5秒，更快检测无数据包情况
        private const int LONG_STALL_THRESHOLD_SECONDS = 5; // 长时间卡顿阈值（秒）
        private const int RECOVERY_COOLDOWN_SECONDS = 10; // 恢复冷却时间（秒），避免频繁重连
        private const int MAX_RECOVERY_ATTEMPTS = 3; // 最大恢复尝试次数
        private const int KEYFRAME_REQUEST_THRESHOLD = 2; // 关键帧请求阈值（连续失败次数）
        private const int KEYFRAME_REQUEST_COOLDOWN_SECONDS = 1; // 关键帧请求冷却时间（秒）
        // 功能开关：受控启用重连；改进后重新启用，但添加更严格的超时和错误处理
        private const bool ENABLE_TAKION_RECONNECT = true;
        
        // ✅ 熔断机制常量
        private const int CIRCUIT_BREAKER_FAILURE_THRESHOLD = 3; // 连续失败次数达到此值后熔断
        private const int CIRCUIT_BREAKER_COOLDOWN_MINUTES = 5; // 熔断冷却时间（分钟）
        
        // ✅ 阶段性超时常量
        private const int KEYFRAME_STEP_TIMEOUT_SECONDS = 2; // 关键帧请求步骤超时（秒）
        private const int RESET_STEP_TIMEOUT_SECONDS = 3; // 重置流状态步骤超时（秒）
        private const int RECONNECT_STEP_TIMEOUT_SECONDS = 12; // 重连步骤超时（秒）
        private const int TOTAL_RECOVERY_TIMEOUT_SECONDS = 15; // 总恢复流程超时（秒）

        #endregion

        #region Fields

        private readonly ILogger<EmergencyRecoveryService> _logger;
        private readonly Func<Task<bool>> _reconnectTakionCallback; // 重建 Takion 连接回调
        private readonly Func<Task> _resetStreamStateCallback; // 重置流状态回调
        private readonly Func<Task>? _requestKeyframeCallback; // 请求关键帧回调（可选）
        private readonly Action<EmergencyRecoveryEvent>? _recoveryEventCallback; // 恢复事件回调
        private readonly Func<Task>? _notifySessionRestartCallback; // ✅ 服务层受控重建通知回调（可选）
        private readonly CancellationToken _cancellationToken; // ✅ 取消令牌，用于程序退出时取消恢复

        private int _consecutiveSevereFailures = 0;
        private DateTime _lastFrameTimestamp = DateTime.MinValue;
        private DateTime _lastRecoveryAttempt = DateTime.MinValue;
        private DateTime _lastKeyframeRequest = DateTime.MinValue;
        private int _recoveryAttemptCount = 0;
        private bool _isRecovering = false;
        private readonly object _lock = new();
        private bool _disposed = false; // ✅ 释放标志
        
        // ✅ 单实例保证：使用 SemaphoreSlim 确保同一时刻最多一个恢复
        private readonly SemaphoreSlim _recoverySemaphore = new SemaphoreSlim(1, 1);
        
        // ✅ 熔断机制
        private int _consecutiveRecoveryFailures = 0; // 连续恢复失败次数
        private DateTime _circuitBreakerUntil = DateTime.MinValue; // 熔断截止时间
        
        // ✅ 静默期机制：恢复失败后一段时间内不再打印日志和触发恢复
        private DateTime _silentUntil = DateTime.MinValue; // 静默期截止时间
        private const int SILENT_PERIOD_SECONDS = 60; // 恢复失败后的静默期时长（秒）
        private const int RECOVERY_IN_PROGRESS_SILENT_SECONDS = 20; // 恢复进行中的静默期时长（秒）

        #endregion

        #region Constructor

        /// <summary>
        /// 创建 Emergency 恢复服务
        /// </summary>
        /// <param name="logger">日志</param>
        /// <param name="reconnectTakionCallback">重建 Takion 连接回调（返回是否成功）</param>
        /// <param name="resetStreamStateCallback">重置流状态回调</param>
        /// <param name="recoveryEventCallback">恢复事件回调（可选）</param>
        /// <param name="requestKeyframeCallback">请求关键帧回调（可选）</param>
        /// <param name="notifySessionRestartCallback">服务层受控重建通知回调（可选）</param>
        /// <param name="cancellationToken">取消令牌，用于程序退出时取消恢复</param>
        public EmergencyRecoveryService(
            ILogger<EmergencyRecoveryService> logger,
            Func<Task<bool>> reconnectTakionCallback,
            Func<Task> resetStreamStateCallback,
            Action<EmergencyRecoveryEvent>? recoveryEventCallback = null,
            Func<Task>? requestKeyframeCallback = null,
            Func<Task>? notifySessionRestartCallback = null,
            CancellationToken cancellationToken = default)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reconnectTakionCallback = reconnectTakionCallback ?? throw new ArgumentNullException(nameof(reconnectTakionCallback));
            _resetStreamStateCallback = resetStreamStateCallback ?? throw new ArgumentNullException(nameof(resetStreamStateCallback));
            _recoveryEventCallback = recoveryEventCallback;
            _requestKeyframeCallback = requestKeyframeCallback;
            _notifySessionRestartCallback = notifySessionRestartCallback;
            _cancellationToken = cancellationToken;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 处理流健康事件（参考 chiaki-ng 的 stream_connection 状态机）
        /// </summary>
        public void OnStreamHealthEvent(StreamHealthEvent evt)
        {
            lock (_lock)
            {
                // 成功或恢复的帧，重置计数器
                if (evt.Status == FrameProcessStatus.Success || evt.Status == FrameProcessStatus.Recovered)
                {
                    _consecutiveSevereFailures = 0;
                    _lastFrameTimestamp = evt.Timestamp;
                    _recoveryAttemptCount = 0; // 重置恢复尝试计数
                    return;
                }

                // 严重失败（Frozen 或 Dropped）
                if (evt.Status == FrameProcessStatus.Frozen || evt.Status == FrameProcessStatus.Dropped)
                {
                    _consecutiveSevereFailures = evt.ConsecutiveFailures;
                    _lastFrameTimestamp = evt.Timestamp;

                    // ✅ 优先尝试请求关键帧（快速恢复，避免重连）
                    if (_consecutiveSevereFailures >= KEYFRAME_REQUEST_THRESHOLD && 
                        _consecutiveSevereFailures < SEVERE_FAILURE_THRESHOLD)
                    {
                        _ = RequestKeyframeIfNeededAsync(evt);
                    }

                    // ✅ 检查是否需要触发 emergency 恢复（重连）
                    if (ShouldTriggerRecovery())
                    {
                        _ = TriggerRecoveryAsync(evt);
                    }
                }
            }
        }

        /// <summary>
        /// 检查长时间卡顿（无新帧到达）
        /// </summary>
        public void CheckLongStall()
        {
            // ✅ 检查是否已释放或已取消，避免在程序退出时执行
            if (_disposed || _cancellationToken.IsCancellationRequested)
                return;

            // ✅ 使用 TryEnter 避免长时间阻塞
            if (!Monitor.TryEnter(_lock, 100))
            {
                // 拿不到锁，可能是恢复流程正在运行，跳过本次检查
                return;
            }

            try
            {
                if (_lastFrameTimestamp == DateTime.MinValue)
                    return;

                // ✅ 再次检查取消，避免在锁内执行耗时操作
                if (_disposed || _cancellationToken.IsCancellationRequested)
                    return;

                // ✅ 再次检查取消，避免在计算后执行操作
                if (_disposed || _cancellationToken.IsCancellationRequested)
                    return;

                var elapsed = (DateTime.UtcNow - _lastFrameTimestamp).TotalSeconds;
                
                // ✅ 检查静默期和熔断状态：如果在静默期或熔断期内，直接返回，不打印日志，避免死循环
                if (DateTime.UtcNow < _silentUntil || DateTime.UtcNow < _circuitBreakerUntil)
                    return;
                
                if (elapsed > LONG_STALL_THRESHOLD_SECONDS && !_isRecovering)
                {
                    // ✅ 在打印日志之前再次检查，避免程序退出后打印
                    if (_disposed || _cancellationToken.IsCancellationRequested)
                        return;

                    // ✅ 更新连续失败计数，确保恢复可以被触发
                    // 长时间卡顿应该被视为严重失败
                    if (_consecutiveSevereFailures < SEVERE_FAILURE_THRESHOLD)
                    {
                        _consecutiveSevereFailures = SEVERE_FAILURE_THRESHOLD;
                    }

                    _logger.LogWarning("⚠️ Long stall detected: {Elapsed}s since last frame", elapsed);
                    
                    // ✅ 再次检查，避免在创建事件后执行
                    if (_disposed || _cancellationToken.IsCancellationRequested)
                        return;

                    // ✅ 长时间卡顿时，强制触发恢复（忽略冷却时间）
                    // 因为长时间卡顿是严重问题，需要立即处理
                    bool shouldTrigger = false;
                    if (DateTime.UtcNow < _circuitBreakerUntil)
                    {
                        // 熔断期：不触发
                        shouldTrigger = false;
                    }
                    else if (DateTime.UtcNow < _silentUntil)
                    {
                        // 静默期：不触发
                        shouldTrigger = false;
                    }
                    else if (_recoveryAttemptCount >= MAX_RECOVERY_ATTEMPTS)
                    {
                        // 达到最大尝试次数：进入静默期
                        if (_silentUntil == DateTime.MinValue)
                        {
                            _silentUntil = DateTime.UtcNow.AddSeconds(SILENT_PERIOD_SECONDS);
                        }
                        shouldTrigger = false;
                    }
                    else
                    {
                        // ✅ 长时间卡顿时，即使冷却时间未到也触发恢复
                        // 因为长时间卡顿是严重问题，需要立即处理
                        shouldTrigger = true;
                    }

                    if (shouldTrigger)
                    {
                        // 创建虚拟事件触发恢复
                        var stallEvent = new StreamHealthEvent(
                            Timestamp: DateTime.UtcNow,
                            FrameIndex: 0,
                            Status: FrameProcessStatus.Dropped,
                            ConsecutiveFailures: _consecutiveSevereFailures,
                            Message: $"Long stall: {elapsed:F1}s",
                            ReusedLastFrame: false,
                            RecoveredByFec: false
                        );

                        // ✅ 异步触发，不等待，避免阻塞
                        _ = TriggerRecoveryAsync(stallEvent);
                    }
                }
            }
            finally
            {
                Monitor.Exit(_lock);
            }
        }

        /// <summary>
        /// 重置恢复状态（用于手动重置）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _consecutiveSevereFailures = 0;
                _recoveryAttemptCount = 0;
                _lastFrameTimestamp = DateTime.MinValue;
                _lastRecoveryAttempt = DateTime.MinValue;
                _lastKeyframeRequest = DateTime.MinValue;
                _isRecovering = false;
                _consecutiveRecoveryFailures = 0; // ✅ 重置连续失败计数
                _circuitBreakerUntil = DateTime.MinValue; // ✅ 重置熔断状态
                _silentUntil = DateTime.MinValue; // ✅ 重置静默期
                _logger.LogDebug("Emergency recovery service reset");
            }
        }

        /// <summary>
        /// 获取恢复统计信息
        /// </summary>
        public EmergencyRecoveryStats GetStats()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                return new EmergencyRecoveryStats
                {
                    ConsecutiveSevereFailures = _consecutiveSevereFailures,
                    RecoveryAttemptCount = _recoveryAttemptCount,
                    LastRecoveryAttempt = _lastRecoveryAttempt,
                    IsRecovering = _isRecovering,
                    SecondsSinceLastFrame = _lastFrameTimestamp == DateTime.MinValue 
                        ? -1 
                        : (now - _lastFrameTimestamp).TotalSeconds,
                    IsInSilentPeriod = now < _silentUntil, // ✅ 是否在静默期
                    IsCircuitBreakerActive = now < _circuitBreakerUntil // ✅ 是否在熔断期
                };
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 判断是否应该触发恢复（参考 chiaki-ng）
        /// </summary>
        private bool ShouldTriggerRecovery()
        {
            // ✅ 检查熔断状态
            if (DateTime.UtcNow < _circuitBreakerUntil)
            {
                // 静默期：不打印日志，避免死循环
                return false;
            }

            // ✅ 检查静默期：恢复失败后一段时间内不再触发恢复
            if (DateTime.UtcNow < _silentUntil)
            {
                // 静默期：不打印日志，避免死循环
                return false;
            }

            // 检查冷却时间
            if (DateTime.UtcNow - _lastRecoveryAttempt < TimeSpan.FromSeconds(RECOVERY_COOLDOWN_SECONDS))
            {
                return false;
            }

            // 检查最大尝试次数
            if (_recoveryAttemptCount >= MAX_RECOVERY_ATTEMPTS)
            {
                // 达到最大尝试次数后，进入静默期，不再打印日志
                if (_silentUntil == DateTime.MinValue)
                {
                    _silentUntil = DateTime.UtcNow.AddSeconds(SILENT_PERIOD_SECONDS);
                }
                return false;
            }

            // 检查严重失败阈值
            return _consecutiveSevereFailures >= SEVERE_FAILURE_THRESHOLD;
        }

        /// <summary>
        /// 请求关键帧（快速恢复尝试）
        /// </summary>
        private async Task RequestKeyframeIfNeededAsync(StreamHealthEvent evt)
        {
            // 检查冷却时间
            if (DateTime.UtcNow - _lastKeyframeRequest < TimeSpan.FromSeconds(KEYFRAME_REQUEST_COOLDOWN_SECONDS))
            {
                return;
            }

            if (_requestKeyframeCallback == null)
            {
                return;
            }

            _lastKeyframeRequest = DateTime.UtcNow;
            _logger.LogWarning("🎯 Requesting keyframe for recovery: consecutive={Consecutive}, frame={Frame}, status={Status}",
                _consecutiveSevereFailures, evt.FrameIndex, evt.Status);

            try
            {
                await _requestKeyframeCallback();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Failed to request keyframe");
            }
        }

        /// <summary>
        /// 触发恢复流程（参考 chiaki-ng 的 stream_connection 状态机）
        /// ✅ 增强版：单实例保证 + 阶段性超时 + 熔断机制 + 服务层通知
        /// </summary>
        private async Task TriggerRecoveryAsync(StreamHealthEvent evt)
        {
            // ✅ 检查是否已释放或已取消
            if (_disposed || _cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Recovery skipped: service disposed or cancellation requested");
                return;
            }

            // ✅ 单实例保证：使用 SemaphoreSlim 确保同一时刻最多一个恢复
            // 注意：使用超时 0 可能导致恢复被跳过，改为使用短超时（100ms）
            try
            {
                if (!await _recoverySemaphore.WaitAsync(100, _cancellationToken))
                {
                    _logger.LogWarning("⚠️ Recovery already in progress, skipping (semaphore timeout)");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Recovery skipped: cancellation requested");
                return;
            }

            try
            {
                lock (_lock)
                {
                    if (_isRecovering)
                    {
                        _logger.LogDebug("Recovery already in progress (double-check), skipping");
                        return;
                    }
                    _isRecovering = true;
                    _lastRecoveryAttempt = DateTime.UtcNow;
                    _recoveryAttemptCount++;
                    
                    // ✅ 恢复触发后立即进入短期静默期，避免在恢复进行中频繁打印日志
                    _silentUntil = DateTime.UtcNow.AddSeconds(RECOVERY_IN_PROGRESS_SILENT_SECONDS);
                }

                _logger.LogWarning("🚨 Emergency recovery triggered (attempt {Attempt}/{Max}): consecutive={Consecutive}, frame={Frame}, status={Status}",
                    _recoveryAttemptCount, MAX_RECOVERY_ATTEMPTS, _consecutiveSevereFailures, evt.FrameIndex, evt.Status);

                // ✅ 总超时控制：整个恢复流程最多 15 秒，同时响应程序退出取消
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellationToken,
                    new CancellationTokenSource(TimeSpan.FromSeconds(TOTAL_RECOVERY_TIMEOUT_SECONDS)).Token);
                var token = cts.Token;

                bool recoverySuccess = false;
                string failureReason = "";

                try
                {
                    // ✅ 发送恢复开始事件
                    _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = EmergencyRecoveryEventType.Started,
                        Attempt = _recoveryAttemptCount,
                        Reason = evt.Message ?? $"Consecutive failures: {_consecutiveSevereFailures}"
                    });

                    // ✅ 步骤 0: 先尝试请求关键帧（快速恢复尝试，超时 2 秒）
                    if (_requestKeyframeCallback != null)
                    {
                        _logger.LogInformation("Step 0: Requesting keyframe before recovery...");
                        try
                        {
                            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(
                                _cancellationToken,
                                new CancellationTokenSource(TimeSpan.FromSeconds(KEYFRAME_STEP_TIMEOUT_SECONDS)).Token);
                            await _requestKeyframeCallback().WaitAsync(stepCts.Token);
                            await Task.Delay(500, token); // 等待关键帧到达
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogWarning("⚠️ Step 0 timeout ({Timeout}s) or cancelled, continuing with recovery", KEYFRAME_STEP_TIMEOUT_SECONDS);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ Keyframe request failed, continuing with recovery");
                        }
                    }

                    // ✅ 步骤 1: 重置流状态（超时 3 秒）
                    _logger.LogInformation("Step 1: Resetting stream state...");
                    try
                    {
                        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(
                            _cancellationToken,
                            new CancellationTokenSource(TimeSpan.FromSeconds(RESET_STEP_TIMEOUT_SECONDS)).Token);
                        await _resetStreamStateCallback().WaitAsync(stepCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("⚠️ Step 1 timeout ({Timeout}s) or cancelled, continuing with recovery", RESET_STEP_TIMEOUT_SECONDS);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "⚠️ Step 1 failed, continuing with recovery");
                    }
                    
                    // ✅ 步骤 2:（可选）重建 Takion 连接（超时 12 秒）
                    bool reconnectSuccess = true;
                    if (ENABLE_TAKION_RECONNECT)
                    {
                        _logger.LogWarning("🔄 Step 2: Reconnecting Takion connection (this will reset the connection and may take up to {Timeout}s)...", RECONNECT_STEP_TIMEOUT_SECONDS);
                        try
                        {
                            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(
                                _cancellationToken,
                                new CancellationTokenSource(TimeSpan.FromSeconds(RECONNECT_STEP_TIMEOUT_SECONDS)).Token);
                            reconnectSuccess = await _reconnectTakionCallback().WaitAsync(stepCts.Token);
                            if (reconnectSuccess)
                            {
                                _logger.LogWarning("✅ Step 2: Takion reconnection successful");
                            }
                            else
                            {
                                _logger.LogError("❌ Step 2: Takion reconnection returned false");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogError("❌ Step 2 timeout ({Timeout}s) or cancelled, reconnection failed", RECONNECT_STEP_TIMEOUT_SECONDS);
                            reconnectSuccess = false;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Step 2 exception, reconnection failed");
                            reconnectSuccess = false;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Step 2: Skipped (Takion reconnect disabled, using light recovery only)");
                    }

                    // ✅ 改进恢复成功判断：如果重连被禁用，恢复成功取决于重置流状态是否成功
                    // 注意：即使重连被禁用，重置流状态和请求关键帧也可能有效
                    // 但我们需要在恢复后检查流是否真正恢复（通过检查是否有新帧）
                    if (ENABLE_TAKION_RECONNECT)
                    {
                        recoverySuccess = reconnectSuccess;
                        failureReason = reconnectSuccess ? "" : "Takion reconnection failed or timeout";
                    }
                    else
                    {
                        // ✅ 轻量恢复模式：只执行了重置流状态和请求关键帧
                        // 恢复是否成功需要在恢复后通过检查流状态来判断
                        // 这里先标记为成功，但会在恢复后通过检查流状态来验证
                        recoverySuccess = true; // 轻量恢复总是"成功"，但实际效果需要验证
                        failureReason = "";
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("❌ Emergency recovery total timeout ({Timeout}s), aborting", TOTAL_RECOVERY_TIMEOUT_SECONDS);
                    recoverySuccess = false;
                    failureReason = $"Total recovery timeout ({TOTAL_RECOVERY_TIMEOUT_SECONDS}s)";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Emergency recovery exception");
                    recoverySuccess = false;
                    failureReason = $"Exception: {ex.Message}";
                }

                // ✅ 处理恢复结果
                if (recoverySuccess)
                {
                    _logger.LogInformation("✅ Emergency recovery completed successfully");
                    
                lock (_lock)
                {
                    _consecutiveSevereFailures = 0;
                    _recoveryAttemptCount = 0; // 成功则重置尝试计数
                    _consecutiveRecoveryFailures = 0; // 重置连续失败计数
                    // ✅ 不立即重置静默期，保持静默期直到其自然过期，给恢复一些时间生效
                    // 如果恢复后立即有新帧，_lastFrameTimestamp 会更新，CheckLongStall 不会触发
                    // 如果恢复后仍然没有新帧，静默期可以防止频繁打印日志
                }

                    // ✅ 发送恢复成功事件
                    _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = EmergencyRecoveryEventType.Succeeded,
                        Attempt = _recoveryAttemptCount,
                        Reason = "Recovery successful"
                    });
                }
                else
                {
                    _logger.LogError("❌ Emergency recovery failed: {Reason}", failureReason);
                    
                    lock (_lock)
                    {
                        _consecutiveRecoveryFailures++;
                        
                        // ✅ 进入静默期：恢复失败后一段时间内不再打印日志和触发恢复
                        _silentUntil = DateTime.UtcNow.AddSeconds(SILENT_PERIOD_SECONDS);
                        
                        // ✅ 熔断机制：连续失败达到阈值后，禁用恢复一段时间
                        if (_consecutiveRecoveryFailures >= CIRCUIT_BREAKER_FAILURE_THRESHOLD)
                        {
                            _circuitBreakerUntil = DateTime.UtcNow.AddMinutes(CIRCUIT_BREAKER_COOLDOWN_MINUTES);
                            _logger.LogError("🔒 Circuit breaker activated: {Failures} consecutive failures, recovery disabled for {Minutes} minutes (silent for {SilentSeconds}s)",
                                _consecutiveRecoveryFailures, CIRCUIT_BREAKER_COOLDOWN_MINUTES, SILENT_PERIOD_SECONDS);
                            
                            // ✅ 通知服务层进行受控重建（而不是底层直接停流）
                            if (_notifySessionRestartCallback != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        _logger.LogWarning("📢 Notifying service layer for controlled session restart...");
                                        await _notifySessionRestartCallback();
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "❌ Failed to notify service layer for session restart");
                                    }
                                });
                            }
                        }
                        else
                        {
                            // ✅ 未达到熔断阈值，但进入静默期，避免频繁打印日志
                            _logger.LogWarning("🔇 Entering silent period for {Seconds}s after recovery failure (failures={Failures}/{Threshold})",
                                SILENT_PERIOD_SECONDS, _consecutiveRecoveryFailures, CIRCUIT_BREAKER_FAILURE_THRESHOLD);
                        }
                    }
                    
                    // ✅ 发送恢复失败事件
                    _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = EmergencyRecoveryEventType.Failed,
                        Attempt = _recoveryAttemptCount,
                        Reason = failureReason
                    });
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isRecovering = false;
                }
                _recoverySemaphore.Release();
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，确保程序退出时不会阻塞
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                // ✅ 释放 SemaphoreSlim，避免阻塞程序退出
                // 如果正在恢复，强制释放（最多等待 1 秒）
                if (_recoverySemaphore.CurrentCount == 0)
                {
                    // 有恢复正在进行，尝试等待释放（最多 1 秒）
                    try
                    {
                        if (_recoverySemaphore.Wait(1000))
                        {
                            _recoverySemaphore.Release();
                        }
                    }
                    catch { }
                }
                
                _recoverySemaphore.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error disposing EmergencyRecoveryService");
            }
        }

        #endregion
    }
}

