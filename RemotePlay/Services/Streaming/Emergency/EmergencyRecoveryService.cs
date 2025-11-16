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
    public class EmergencyRecoveryService
    {
        #region Constants

        // ✅ 恢复阈值（参考 chiaki-ng）
        // ✅ 降低阈值以更快响应：从5次降低到3次，更快触发恢复
        private const int SEVERE_FAILURE_THRESHOLD = 3; // 连续严重失败次数
        // ✅ 缩短长时间卡顿阈值：从10秒降低到5秒，更快检测无数据包情况
        private const int LONG_STALL_THRESHOLD_SECONDS = 5; // 长时间卡顿阈值（秒）
        private const int RECOVERY_COOLDOWN_SECONDS = 30; // 恢复冷却时间（秒），避免频繁重连
        private const int MAX_RECOVERY_ATTEMPTS = 3; // 最大恢复尝试次数
        private const int KEYFRAME_REQUEST_THRESHOLD = 2; // 关键帧请求阈值（连续失败次数）
        private const int KEYFRAME_REQUEST_COOLDOWN_SECONDS = 1; // 关键帧请求冷却时间（秒）

        #endregion

        #region Fields

        private readonly ILogger<EmergencyRecoveryService> _logger;
        private readonly Func<Task<bool>> _reconnectTakionCallback; // 重建 Takion 连接回调
        private readonly Func<Task> _resetStreamStateCallback; // 重置流状态回调
        private readonly Func<Task>? _requestKeyframeCallback; // 请求关键帧回调（可选）
        private readonly Action<EmergencyRecoveryEvent>? _recoveryEventCallback; // 恢复事件回调

        private int _consecutiveSevereFailures = 0;
        private DateTime _lastFrameTimestamp = DateTime.MinValue;
        private DateTime _lastRecoveryAttempt = DateTime.MinValue;
        private DateTime _lastKeyframeRequest = DateTime.MinValue;
        private int _recoveryAttemptCount = 0;
        private bool _isRecovering = false;
        private readonly object _lock = new();

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
        public EmergencyRecoveryService(
            ILogger<EmergencyRecoveryService> logger,
            Func<Task<bool>> reconnectTakionCallback,
            Func<Task> resetStreamStateCallback,
            Action<EmergencyRecoveryEvent>? recoveryEventCallback = null,
            Func<Task>? requestKeyframeCallback = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reconnectTakionCallback = reconnectTakionCallback ?? throw new ArgumentNullException(nameof(reconnectTakionCallback));
            _resetStreamStateCallback = resetStreamStateCallback ?? throw new ArgumentNullException(nameof(resetStreamStateCallback));
            _recoveryEventCallback = recoveryEventCallback;
            _requestKeyframeCallback = requestKeyframeCallback;
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
            lock (_lock)
            {
                if (_lastFrameTimestamp == DateTime.MinValue)
                    return;

                var elapsed = (DateTime.UtcNow - _lastFrameTimestamp).TotalSeconds;
                if (elapsed > LONG_STALL_THRESHOLD_SECONDS && !_isRecovering)
                {
                    _logger.LogWarning("⚠️ Long stall detected: {Elapsed}s since last frame", elapsed);
                    
                    // 创建虚拟事件触发恢复
                    var stallEvent = new StreamHealthEvent(
                        Timestamp: DateTime.UtcNow,
                        FrameIndex: 0,
                        Status: FrameProcessStatus.Dropped,
                        ConsecutiveFailures: _consecutiveSevereFailures + 1,
                        Message: $"Long stall: {elapsed:F1}s",
                        ReusedLastFrame: false,
                        RecoveredByFec: false
                    );

                    if (ShouldTriggerRecovery())
                    {
                        _ = TriggerRecoveryAsync(stallEvent);
                    }
                }
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
                return new EmergencyRecoveryStats
                {
                    ConsecutiveSevereFailures = _consecutiveSevereFailures,
                    RecoveryAttemptCount = _recoveryAttemptCount,
                    LastRecoveryAttempt = _lastRecoveryAttempt,
                    IsRecovering = _isRecovering,
                    SecondsSinceLastFrame = _lastFrameTimestamp == DateTime.MinValue 
                        ? -1 
                        : (DateTime.UtcNow - _lastFrameTimestamp).TotalSeconds
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
            // 检查冷却时间
            if (DateTime.UtcNow - _lastRecoveryAttempt < TimeSpan.FromSeconds(RECOVERY_COOLDOWN_SECONDS))
            {
                return false;
            }

            // 检查最大尝试次数
            if (_recoveryAttemptCount >= MAX_RECOVERY_ATTEMPTS)
            {
                _logger.LogError("❌ Maximum recovery attempts ({Max}) reached, stopping recovery", MAX_RECOVERY_ATTEMPTS);
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
        /// </summary>
        private async Task TriggerRecoveryAsync(StreamHealthEvent evt)
        {
            if (_isRecovering)
            {
                _logger.LogDebug("Recovery already in progress, skipping");
                return;
            }

            _isRecovering = true;
            _lastRecoveryAttempt = DateTime.UtcNow;
            _recoveryAttemptCount++;

            _logger.LogWarning("🚨 Emergency recovery triggered (attempt {Attempt}/{Max}): consecutive={Consecutive}, frame={Frame}, status={Status}",
                _recoveryAttemptCount, MAX_RECOVERY_ATTEMPTS, _consecutiveSevereFailures, evt.FrameIndex, evt.Status);

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

                // ✅ 步骤 0: 先尝试请求关键帧（快速恢复尝试）
                if (_requestKeyframeCallback != null)
                {
                    _logger.LogInformation("Step 0: Requesting keyframe before recovery...");
                    try
                    {
                        await _requestKeyframeCallback();
                        await Task.Delay(500); // 等待关键帧到达
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Keyframe request failed, continuing with recovery");
                    }
                }

                // ✅ 步骤 1: 重置流状态（参考 chiaki-ng: stream_connection 状态重置）
                _logger.LogInformation("Step 1: Resetting stream state...");
                await _resetStreamStateCallback();

                // ✅ 步骤 2: 重建 Takion 连接（参考 chiaki-ng: chiaki_stream_connection_run）
                _logger.LogInformation("Step 2: Reconnecting Takion connection...");
                bool reconnectSuccess = await _reconnectTakionCallback();

                if (reconnectSuccess)
                {
                    _logger.LogInformation("✅ Emergency recovery completed successfully");
                    
                    // 重置计数器
                    _consecutiveSevereFailures = 0;
                    _recoveryAttemptCount = 0; // 成功则重置尝试计数

                    // ✅ 发送恢复成功事件
                    _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = EmergencyRecoveryEventType.Succeeded,
                        Attempt = _recoveryAttemptCount,
                        Reason = "Takion reconnection successful"
                    });
                }
                else
                {
                    _logger.LogError("❌ Emergency recovery failed: Takion reconnection failed");
                    
                    // ✅ 发送恢复失败事件
                    _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Type = EmergencyRecoveryEventType.Failed,
                        Attempt = _recoveryAttemptCount,
                        Reason = "Takion reconnection failed"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Emergency recovery exception");
                
                // ✅ 发送恢复异常事件
                _recoveryEventCallback?.Invoke(new EmergencyRecoveryEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Type = EmergencyRecoveryEventType.Failed,
                    Attempt = _recoveryAttemptCount,
                    Reason = $"Exception: {ex.Message}"
                });
            }
            finally
            {
                _isRecovering = false;
            }
        }

        #endregion
    }
}

