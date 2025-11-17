using System;
using RemotePlay.Services.Streaming.AV;

namespace RemotePlay.Models.Streaming
{
    public readonly record struct StreamHealthEvent(
        DateTime Timestamp,
        int FrameIndex,
        FrameProcessStatus Status,
        int ConsecutiveFailures,
        string? Message,
        bool ReusedLastFrame,
        bool RecoveredByFec);

    public enum FrameProcessStatus
    {
        Success,
        Recovered,
        FecSuccess,
        FecFailed,
        Frozen,
        Dropped
    }

    public readonly record struct FrameProcessInfo(
        int FrameIndex,
        FrameProcessStatus Status,
        bool RecoveredByFec,
        bool ReusedLastFrame,
        string? Reason);
    public readonly record struct StreamHealthSnapshot
    {
        public DateTime Timestamp { get; init; }
        public FrameProcessStatus LastStatus { get; init; }
        public string? Message { get; init; }
        public int ConsecutiveFailures { get; init; }
        public int TotalRecoveredFrames { get; init; }
        public int TotalFrozenFrames { get; init; }
        public int TotalDroppedFrames { get; init; }
        public int DeltaRecoveredFrames { get; init; }
        public int DeltaFrozenFrames { get; init; }
        public int DeltaDroppedFrames { get; init; }
        public int RecentWindowSeconds { get; init; }
        public int RecentSuccessFrames { get; init; }
        public int RecentRecoveredFrames { get; init; }
        public int RecentFrozenFrames { get; init; }
        public int RecentDroppedFrames { get; init; }
        public double RecentFps { get; init; }
        public double AverageFrameIntervalMs { get; init; }
        public DateTime LastFrameTimestampUtc { get; init; }
        
        // ✅ 流统计和码率（参考 chiaki-ng 的 ChiakiStreamStats）
        public ulong TotalFrames { get; init; } // 总帧数
        public ulong TotalBytes { get; init; } // 总字节数
        public double MeasuredBitrateMbps { get; init; } // 测量码率（Mbps）

        // ✅ 帧丢失统计（参考 chiaki-ng 的 frames_lost）
        // 语义：自上次快照以来丢失的帧数量（delta），便于窗口化观察
        public int FramesLost { get; init; }

        // ✅ 上一个至少部分解码的帧索引（参考 chiaki-ng 的 frame_index_prev）
        public int FrameIndexPrev { get; init; }
    }

    public readonly record struct StreamPipelineStats
    {
        public int VideoReceived { get; init; }
        public int VideoLost { get; init; }
        public int VideoTimeoutDropped { get; init; } // ✅ 视频帧超时丢弃数（参考 chiaki-ng）
        public int AudioReceived { get; init; }
        public int AudioLost { get; init; }
        public int AudioTimeoutDropped { get; init; } // ✅ 音频帧超时丢弃数（参考 chiaki-ng）
        public int PendingPackets { get; init; }
        public int TotalIdrRequests { get; init; }
        public int IdrRequestsRecent { get; init; }
        public int IdrRequestWindowSeconds { get; init; }
        public DateTime? LastIdrRequestUtc { get; init; }
        public int FecAttempts { get; init; }
        public int FecSuccess { get; init; }
        public int FecFailures { get; init; }
        public double FecSuccessRate { get; init; }
        public double FrameOutputFps { get; init; }
        public double FrameIntervalMs { get; init; }
    }
}

