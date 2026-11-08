using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// WebRTC 视频发送管道：单线程按序发送，任何丢帧都进入"等待 IDR"状态，直到收到关键帧才恢复发送。
    /// </summary>
    internal sealed class VideoPipeline : IDisposable
    {
        // 上游（重排/FEC）偶尔会一次性吐出多帧，队列太小会把短暂突发误判为发送瓶颈
        private const int QUEUE_CAPACITY = 30;
        private static readonly TimeSpan SlowSendThreshold = TimeSpan.FromMilliseconds(50);
        private const uint VIDEO_CLOCK_RATE = 90000;
        private const uint DEFAULT_FRAME_DURATION = VIDEO_CLOCK_RATE / 60;
        private const uint MIN_FRAME_DURATION = VIDEO_CLOCK_RATE / 240;
        private const uint MAX_FRAME_DURATION = VIDEO_CLOCK_RATE;
        private static readonly TimeSpan KeyframeRetryInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxWaitForIdr = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StatsLogInterval = TimeSpan.FromSeconds(5);

        private readonly ILogger? _logger;
        private readonly RTCPeerConnection _peerConnection;
        private readonly Channel<VideoFrame> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _sendTask;
        private readonly object _stateLock = new();

        private volatile bool _isHevc;
        private volatile bool _waitingForIdr = true;
        private long _waitingSinceTicks;
        private long _lastKeyframeRequestTicks;

        private long _lastSentArrivalTicks;
        private long _frameIndex;
        private long _sentCount;
        private long _failedCount;
        private long _droppedCount;
        private long _lastStatsLogTicks;
        private long _lastSlowSendLogTicks;

        // 诊断窗口统计（除 _windowIdrReceived 外只在发送线程中读写）
        private long _windowIdrReceived;
        private int _windowFrames;
        private int _windowIdrSent;
        private long _windowBytes;
        private int _windowMaxFrameBytes;
        private uint _windowMinDuration = uint.MaxValue;
        private uint _windowMaxDuration;
        private double _windowMaxSendMs;

        private Action<long>? _onPacketSent;
        private Action? _onKeyframeRequest;
        private Func<string>? _diagnosticsSuffix;

        public VideoPipeline(ILogger? logger, RTCPeerConnection peerConnection, string codec)
        {
            _logger = logger;
            _peerConnection = peerConnection ?? throw new ArgumentNullException(nameof(peerConnection));
            SetCodec(codec);

            _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(QUEUE_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

            _waitingSinceTicks = Stopwatch.GetTimestamp();
            _sendTask = Task.Run(SendLoopAsync);
        }

        public void SetOnPacketSent(Action<long> callback) => _onPacketSent = callback;

        public void SetOnKeyframeRequest(Action callback) => _onKeyframeRequest = callback;

        /// <summary>
        /// 附加到周期性诊断日志末尾的额外信息（例如 RTCP 统计）
        /// </summary>
        public void SetDiagnosticsSuffix(Func<string> suffix) => _diagnosticsSuffix = suffix;

        public void SetCodec(string? codec)
        {
            var normalized = codec?.Trim().ToLowerInvariant();
            _isHevc = normalized is "hevc" or "h265";
        }

        /// <summary>
        /// 强制进入等待 IDR 状态（例如切换接收器后），并请求关键帧。
        /// </summary>
        public void EnterWaitForIdr(string reason)
        {
            lock (_stateLock)
            {
                if (!_waitingForIdr)
                {
                    _waitingForIdr = true;
                    _waitingSinceTicks = Stopwatch.GetTimestamp();
                }
            }
            RequestKeyframe(reason, force: true);
        }

        /// <summary>
        /// 接收一帧（packet[0] 为 header 类型字节）。由单一生产者线程调用。
        /// </summary>
        public bool OnFrame(byte[] packet, bool isKeyFrameHint)
        {
            if (packet == null || packet.Length <= 1 || _cts.IsCancellationRequested)
            {
                return false;
            }

            var payload = packet.AsSpan(1);
            bool isIdr = isKeyFrameHint || (_waitingForIdr && ContainsIdrNal(payload, _isHevc));
            long now = Stopwatch.GetTimestamp();
            if (isIdr)
            {
                Interlocked.Increment(ref _windowIdrReceived);
            }

            lock (_stateLock)
            {
                if (_waitingForIdr && !isIdr)
                {
                    Interlocked.Increment(ref _droppedCount);
                    if (Stopwatch.GetElapsedTime(_waitingSinceTicks, now) > MaxWaitForIdr)
                    {
                        _logger?.LogWarning("⚠️ No keyframe after waiting {Seconds}s for IDR, resuming non-key frames", MaxWaitForIdr.TotalSeconds);
                        _waitingForIdr = false;
                    }
                    else
                    {
                        RequestKeyframe("waiting-for-idr", force: false);
                        return false;
                    }
                }

                if (isIdr)
                {
                    // 关键帧之前排队的帧都已过时，直接丢弃以降低延迟
                    int cleared = 0;
                    while (_channel.Reader.TryRead(out _))
                    {
                        cleared++;
                    }
                    if (cleared > 0)
                    {
                        Interlocked.Add(ref _droppedCount, cleared);
                    }
                    _waitingForIdr = false;
                }

                var frame = new VideoFrame(payload.ToArray(), isIdr, ++_frameIndex, now);
                if (_channel.Writer.TryWrite(frame))
                {
                    return true;
                }

                // 队列已满：丢弃当前帧并进入等待 IDR，避免把断链的 P 帧送给解码器
                Interlocked.Increment(ref _droppedCount);
                _waitingForIdr = true;
                _waitingSinceTicks = now;
                _logger?.LogWarning("⚠️ WebRTC video send queue full ({Capacity} frames), waiting for keyframe", QUEUE_CAPACITY);
            }

            RequestKeyframe("send-queue-full", force: true);
            return false;
        }

        private void RequestKeyframe(string reason, bool force)
        {
            var callback = _onKeyframeRequest;
            if (callback == null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastKeyframeRequestTicks);
            if (!force && last != 0 && Stopwatch.GetElapsedTime(last, now) < KeyframeRetryInterval)
            {
                return;
            }
            Interlocked.Exchange(ref _lastKeyframeRequestTicks, now);

            _logger?.LogDebug("🎯 Video pipeline requesting keyframe: {Reason}", reason);
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error invoking keyframe request callback");
            }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (var frame in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    if (_peerConnection.connectionState is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                    {
                        Interlocked.Increment(ref _failedCount);
                        continue;
                    }

                    uint duration = ComputeDuration(frame.ArrivalTicks);
                    try
                    {
                        long sendStart = Stopwatch.GetTimestamp();
                        // SendVideo 的第一个参数是"本帧到下一帧的时长"，SIPSorcery 用当前时间戳发送本帧后再累加该值
                        _peerConnection.SendVideo(duration, frame.Data);
                        Interlocked.Increment(ref _sentCount);
                        _onPacketSent?.Invoke(frame.FrameIndex);

                        var sendTime = Stopwatch.GetElapsedTime(sendStart);
                        _windowFrames++;
                        if (frame.IsIdr) _windowIdrSent++;
                        _windowBytes += frame.Data.Length;
                        _windowMaxFrameBytes = Math.Max(_windowMaxFrameBytes, frame.Data.Length);
                        _windowMinDuration = Math.Min(_windowMinDuration, duration);
                        _windowMaxDuration = Math.Max(_windowMaxDuration, duration);
                        _windowMaxSendMs = Math.Max(_windowMaxSendMs, sendTime.TotalMilliseconds);

                        if (sendTime > SlowSendThreshold && Stopwatch.GetElapsedTime(_lastSlowSendLogTicks) > StatsLogInterval)
                        {
                            _lastSlowSendLogTicks = Stopwatch.GetTimestamp();
                            _logger?.LogWarning("⚠️ SendVideo took {Ms:F0}ms (frame size {Size} bytes, IDR={Idr}); sender may be blocked",
                                sendTime.TotalMilliseconds, frame.Data.Length, frame.IsIdr);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedCount);
                        _logger?.LogWarning(ex, "SendVideo failed: FrameIndex={Index}", frame.FrameIndex);
                        EnterWaitForIdr("send-failed");
                    }

                    LogStatsIfDue();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "WebRTC video send loop exited unexpectedly");
            }
        }

        private uint ComputeDuration(long arrivalTicks)
        {
            long previous = _lastSentArrivalTicks;
            _lastSentArrivalTicks = arrivalTicks;
            if (previous == 0)
            {
                return DEFAULT_FRAME_DURATION;
            }

            double rtpUnits = Stopwatch.GetElapsedTime(previous, arrivalTicks).TotalSeconds * VIDEO_CLOCK_RATE;
            return (uint)Math.Clamp(rtpUnits, MIN_FRAME_DURATION, MAX_FRAME_DURATION);
        }

        private void LogStatsIfDue()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastStatsLogTicks == 0)
            {
                _lastStatsLogTicks = now;
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(_lastStatsLogTicks, now);
            if (elapsed < StatsLogInterval)
            {
                return;
            }
            _lastStatsLogTicks = now;

            string suffix = string.Empty;
            try
            {
                suffix = _diagnosticsSuffix?.Invoke() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to get diagnostics suffix");
            }

            _logger?.LogDebug(
                "WebRTC send diagnostics: frames={Frames} (IDR sent={IdrSent}, IDR received={IdrReceived}) bitrate={Kbps:F0}kbps maxFrame={MaxFrame}B " +
                "RTP frame interval={MinDur}-{MaxDur} SendVideo max={MaxSendMs:F1}ms total sent/dropped/failed={Sent}/{Dropped}/{Failed} queue={Queue} waitingForIdr={Waiting} {Suffix}",
                _windowFrames, _windowIdrSent, Interlocked.Exchange(ref _windowIdrReceived, 0),
                _windowBytes * 8 / 1000.0 / elapsed.TotalSeconds, _windowMaxFrameBytes,
                _windowFrames > 0 ? _windowMinDuration : 0, _windowMaxDuration, _windowMaxSendMs,
                Interlocked.Read(ref _sentCount), Interlocked.Read(ref _droppedCount), Interlocked.Read(ref _failedCount),
                _channel.Reader.Count, _waitingForIdr, suffix);

            _windowFrames = 0;
            _windowIdrSent = 0;
            _windowBytes = 0;
            _windowMaxFrameBytes = 0;
            _windowMinDuration = uint.MaxValue;
            _windowMaxDuration = 0;
            _windowMaxSendMs = 0;
        }

        public (long sent, long failed, long dropped, int queueSize) GetStats()
        {
            return (Interlocked.Read(ref _sentCount), Interlocked.Read(ref _failedCount),
                Interlocked.Read(ref _droppedCount), _channel.Reader.Count);
        }

        /// <summary>
        /// 扫描 Annex-B 码流判断是否包含 IDR/IRAP NAL。
        /// </summary>
        internal static bool ContainsIdrNal(ReadOnlySpan<byte> data, bool isHevc)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != 0x00 || data[i + 1] != 0x00)
                {
                    continue;
                }

                int nalStart;
                if (data[i + 2] == 0x01)
                {
                    nalStart = i + 3;
                }
                else if (data[i + 2] == 0x00 && data[i + 3] == 0x01)
                {
                    nalStart = i + 4;
                }
                else
                {
                    continue;
                }

                if (nalStart >= data.Length)
                {
                    return false;
                }

                byte header = data[nalStart];
                if (isHevc)
                {
                    int type = (header >> 1) & 0x3F;
                    if (type is >= 16 and <= 21)
                    {
                        return true;
                    }
                }
                else if ((header & 0x1F) == 5)
                {
                    return true;
                }

                i = nalStart;
            }

            return false;
        }

        public void Dispose()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _cts.Cancel();
            _channel.Writer.TryComplete();
            try
            {
                _sendTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "VideoPipeline: exception while waiting for send task to exit");
            }
            _cts.Dispose();
        }
    }
}
