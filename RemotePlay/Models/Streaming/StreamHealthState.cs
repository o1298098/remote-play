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
    }

    public readonly record struct StreamPipelineStats
    {
        public int VideoReceived { get; init; }
        public int VideoLost { get; init; }
        public int AudioReceived { get; init; }
        public int AudioLost { get; init; }
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

