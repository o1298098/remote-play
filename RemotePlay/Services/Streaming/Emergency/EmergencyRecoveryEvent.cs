using System;

namespace RemotePlay.Services.Streaming.Emergency
{
    /// <summary>
    /// Emergency 恢复事件类型（参考 chiaki-ng）
    /// </summary>
    public enum EmergencyRecoveryEventType
    {
        Started,    // 恢复开始
        Succeeded,  // 恢复成功
        Failed      // 恢复失败
    }

    /// <summary>
    /// Emergency 恢复事件（参考 chiaki-ng 的 stream_connection 事件）
    /// </summary>
    public readonly record struct EmergencyRecoveryEvent
    {
        public DateTime Timestamp { get; init; }
        public EmergencyRecoveryEventType Type { get; init; }
        public int Attempt { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Emergency 恢复统计信息
    /// </summary>
    public readonly record struct EmergencyRecoveryStats
    {
        public int ConsecutiveSevereFailures { get; init; }
        public int RecoveryAttemptCount { get; init; }
        public DateTime LastRecoveryAttempt { get; init; }
        public bool IsRecovering { get; init; }
        public double SecondsSinceLastFrame { get; init; }
        public bool IsInSilentPeriod { get; init; } // ✅ 是否在静默期
        public bool IsCircuitBreakerActive { get; init; } // ✅ 是否在熔断期
    }
}

