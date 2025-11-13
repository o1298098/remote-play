using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming;
using RemotePlay.Utils.Crypto;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 完全优化的 AVHandler
    /// 低延迟、高性能、零拷贝、批量处理、线程安全
    /// </summary>
    public sealed class AVHandler
    {
        private readonly ILogger<AVHandler> _logger;
        private readonly string _hostType;
        private StreamCipher? _cipher;
        private IAVReceiver? _receiver;

        private readonly ConcurrentQueue<AVPacket> _queue = new();
        private ReorderQueue<AVPacket>? _videoReorderQueue;
        private uint _videoReorderQueueExpected;
        private const int MaxQueueSize = 5000;
        private volatile bool _waiting = false;

        private const int DirectProcessThreshold = 10;
        private int _directProcessCount = 0;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly CancellationToken _ct;

        private AVStream? _videoStream;
        private AVStream? _audioStream;

        private string? _detectedVideoCodec;
        private string? _detectedAudioCodec;

        private int _videoFrameCounter = 0;
        private Action<int, int>? _videoCorruptCallback;
        private Action<int, int>? _audioCorruptCallback;
        private Action<StreamHealthEvent>? _healthCallback;
        private FrameProcessStatus _lastFrameStatus = FrameProcessStatus.Success;
        private string? _lastHealthMessage;
        private int _consecutiveVideoFailures = 0;
        private int _totalRecoveredFrames = 0;
        private int _totalFrozenFrames = 0;
        private int _totalDroppedFrames = 0;
        private int _deltaRecoveredFrames = 0;
        private int _deltaFrozenFrames = 0;
        private int _deltaDroppedFrames = 0;
        private DateTime _lastHealthTimestamp = DateTime.MinValue;
        private DateTime _lastFrameTimestampUtc = DateTime.MinValue;
        private readonly Queue<(DateTime Timestamp, FrameProcessStatus Status)> _recentFrameStatuses = new();
        private readonly Queue<(DateTime Timestamp, double IntervalMs)> _recentFrameIntervals = new();
        private double _recentIntervalSumMs = 0;
        private readonly TimeSpan _healthWindow = TimeSpan.FromSeconds(10);
        private readonly object _healthLock = new();

        public AVHandler(
            ILogger<AVHandler> logger,
            string hostType,
            StreamCipher? cipher,
            IAVReceiver? receiver,
            CancellationToken ct)
        {
            _logger = logger;
            _hostType = hostType;
            _cipher = cipher;
            _receiver = receiver;
            _ct = ct;
            ResetVideoReorderQueue();
            ResetHealthState();
        }

        #region Receiver / Cipher / Headers

        public void SetReceiver(IAVReceiver receiver)
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));

            var oldReceiver = _receiver;
            _receiver = receiver;

            if (oldReceiver != null)
                _logger.LogInformation("🔄 Switching receiver: {Old} -> {New}", oldReceiver.GetType().Name, receiver.GetType().Name);

            if (_videoStream != null || _audioStream != null)
            {
                var videoHeader = _videoStream?.Header ?? Array.Empty<byte>();
                var audioHeader = _audioStream?.Header ?? Array.Empty<byte>();
                try { receiver.OnStreamInfo(videoHeader, audioHeader); } catch { }
            }

            if (_detectedVideoCodec != null) receiver.SetVideoCodec(_detectedVideoCodec);
            if (_detectedAudioCodec != null) receiver.SetAudioCodec(_detectedAudioCodec);
        }

        public void SetCipher(StreamCipher cipher)
        {
            _cipher = cipher;
            if (_receiver != null)
            {
                if (_workerTask == null || _workerTask.IsCompleted)
                    StartWorker();
            }
            else
            {
                _logger.LogWarning("⚠️ SetCipher called but receiver is null");
            }
        }

        public void SetHeaders(byte[]? videoHeader, byte[]? audioHeader, ILoggerFactory loggerFactory)
        {
            if (_receiver == null)
            {
                _logger.LogWarning("⚠️ Cannot set headers: receiver is null");
                return;
            }

            ResetVideoReorderQueue();
            ResetHealthState();

            _videoStream = new AVStream(
                "video",
                videoHeader ?? Array.Empty<byte>(),
                HandleVideoFrame,
                InvokeVideoCorrupt,
                HandleVideoFrameResult,
                loggerFactory.CreateLogger<AVStream>());

            _audioStream = new AVStream(
                "audio",
                audioHeader ?? Array.Empty<byte>(),
                frame =>
                {
                    var outBuf = ArrayPool<byte>.Shared.Rent(1 + frame.Length);
                    outBuf[0] = (byte)HeaderType.AUDIO;
                    frame.AsSpan().CopyTo(outBuf.AsSpan(1));
                    try { _receiver?.OnAudioPacket(outBuf.AsSpan(0, frame.Length + 1).ToArray()); } finally { ArrayPool<byte>.Shared.Return(outBuf); }
                },
                InvokeAudioCorrupt,
                null,
                loggerFactory.CreateLogger<AVStream>());

            if (_cipher != null)
            {
                if (_workerTask == null || _workerTask.IsCompleted)
                    StartWorker();
            }
            else
            {
                _logger.LogWarning("⚠️ SetHeaders called but cipher is null");
            }
        }

        #endregion

        #region Packet Handling

        public void AddPacket(byte[] msg)
        {
            if (!AVPacket.TryParse(msg, _hostType, out var packet))
            {
                _logger.LogWarning("⚠️ Failed to parse AV packet, len={Len}", msg.Length);
                return;
            }

            if (packet.Type == HeaderType.VIDEO)
            {
                _videoReorderQueue?.Push(packet);
            }
            else
            {
                HandleOrderedPacket(packet);
            }
        }

        private void ProcessSinglePacket(AVPacket packet)
        {
            byte[] decrypted = DecryptPacket(packet);
            if (packet.Type == HeaderType.VIDEO)
            {
                if (_videoStream == null)
                {
                    _logger.LogError("❌ VideoStream null, frame={Frame}", packet.FrameIndex);
                    return;
                }
                _videoStream.Handle(packet, decrypted);
            }
            else
            {
                _audioStream?.Handle(packet, decrypted);
            }
        }

        private byte[] DecryptPacket(AVPacket packet)
        {
            var data = packet.Data;
            if (_cipher != null && data.Length > 0 && packet.KeyPos > 0)
            {
                try { data = _cipher.Decrypt(data, (int)packet.KeyPos); }
                catch (Exception ex) { _logger.LogError(ex, "❌ Decrypt failed frame={Frame}", packet.FrameIndex); }
            }
            return data;
        }

        #endregion

        #region Reorder Queue

        public void SetCorruptFrameCallbacks(Action<int, int>? videoCallback, Action<int, int>? audioCallback = null)
        {
            _videoCorruptCallback = videoCallback;
            _audioCorruptCallback = audioCallback;
        }

        public void SetStreamHealthCallback(Action<StreamHealthEvent>? healthCallback)
        {
            _healthCallback = healthCallback;
        }

        private void ResetHealthState()
        {
            lock (_healthLock)
            {
                _lastFrameStatus = FrameProcessStatus.Success;
                _lastHealthMessage = null;
                _consecutiveVideoFailures = 0;
                _totalRecoveredFrames = 0;
                _totalFrozenFrames = 0;
                _totalDroppedFrames = 0;
                _deltaRecoveredFrames = 0;
                _deltaFrozenFrames = 0;
                _deltaDroppedFrames = 0;
                _lastHealthTimestamp = DateTime.MinValue;
                _lastFrameTimestampUtc = DateTime.MinValue;
                _recentFrameStatuses.Clear();
                _recentFrameIntervals.Clear();
                _recentIntervalSumMs = 0;
            }
        }

        private void ResetVideoReorderQueue()
        {
            _videoReorderQueue = new ReorderQueue<AVPacket>(
                _logger,
                pkt => (uint)pkt.Index,
                HandleOrderedPacket);
            _videoReorderQueueExpected = 0;
        }

        private void HandleOrderedPacket(AVPacket packet)
        {
            bool isVideo = packet.Type == HeaderType.VIDEO;

            if (packet.Type == HeaderType.VIDEO && _detectedVideoCodec == null)
                DetectVideoCodec(packet);
            if (packet.Type == HeaderType.AUDIO && _detectedAudioCodec == null)
                DetectAudioCodec(packet);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Ordered packet: type={Type}, frame={Frame}, unit={Unit}, total={Total}, expected={Expected}, waiting={Waiting}",
                    packet.Type,
                    packet.FrameIndex,
                    packet.UnitIndex,
                    packet.UnitsTotal,
                    _videoReorderQueueExpected,
                    _waiting);
            }

            if (_receiver == null)
                return;

            if (isVideo)
                _videoReorderQueueExpected = (uint)packet.Index;

            if (_queue.Count >= MaxQueueSize)
            {
                while (_queue.TryDequeue(out _)) { }
                _waiting = true;
                _logger.LogWarning("⚠️ AV queue overflow, cleared queue, waiting for unit_index=0");
                ResetVideoReorderQueue();
            }

            if (_waiting)
            {
                if (!isVideo || packet.UnitIndex != 0)
                    return;
                _waiting = false;
            }

            if (_queue.Count < DirectProcessThreshold && _cipher != null)
            {
                try
                {
                    ProcessSinglePacket(packet);
                    Interlocked.Increment(ref _directProcessCount);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Direct processing failed, enqueue instead");
                }
            }

            _queue.Enqueue(packet);

            if (_queue.Count > 100 && (_workerTask == null || _workerTask.IsCompleted) && _cipher != null)
            {
                _logger.LogError("❌ Queue has {Size} packets but worker not running! Starting...", _queue.Count);
                StartWorker();
            }
        }

        private void InvokeVideoCorrupt(int start, int end)
        {
            if (_videoCorruptCallback == null)
                return;
            try
            {
                _videoCorruptCallback.Invoke(start, end);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Video corrupt callback failed (start={Start}, end={End})", start, end);
            }
        }

        private void InvokeAudioCorrupt(int start, int end)
        {
            if (_audioCorruptCallback == null)
                return;
            try
            {
                _audioCorruptCallback.Invoke(start, end);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Audio corrupt callback failed (start={Start}, end={End})", start, end);
            }
        }

        #endregion

        #region Codec Detection

        private void DetectAudioCodec(AVPacket packet)
        {
            string codec = packet.Codec switch
            {
                0x01 or 0x02 => "opus",
                0x03 or 0x04 => "aac",
                _ => "opus"
            };
            if (codec == "opus" && packet.Codec != 0x01 && packet.Codec != 0x02)
                _logger.LogWarning("⚠️ Unknown audio codec 0x{Codec:X2}, defaulting to opus", packet.Codec);

            _detectedAudioCodec = codec;
            _receiver?.SetAudioCodec(codec);
        }

        private void DetectVideoCodec(AVPacket packet)
        {
            string? codec = _videoStream?.Header != null ? DetectCodecFromHeader(_videoStream.Header) : null;

            if (codec != null)
            {
                _detectedVideoCodec = codec;
                _receiver?.SetVideoCodec(codec);
                _logger.LogInformation("📹 Detected video codec: {Codec}", codec);
                return;
            }

            _detectedVideoCodec = packet.Codec switch
            {
                0x06 => "h264",
                0x36 or 0x37 => "hevc",
                _ => "h264"
            };
            _receiver?.SetVideoCodec(_detectedVideoCodec);
        }

        private string? DetectCodecFromHeader(byte[] header)
        {
            int len = Math.Max(header.Length - 64, 0); // 去掉 padding
            for (int i = 0; i < len - 4; i++)
            {
                if (header[i] == 0x00 && header[i + 1] == 0x00)
                {
                    int offset = header[i + 2] == 0x01 ? 3 : (header[i + 2] == 0x00 && header[i + 3] == 0x01 ? 4 : 0);
                    if (offset == 0) continue;
                    byte nal = header[i + offset];
                    if ((nal & 0x7E) == 0x40 || (nal & 0x7E) == 0x42 || (nal & 0x7E) == 0x44) return "hevc";
                    if ((nal & 0x1F) is 5 or 7 or 8) return "h264";
                }
            }
            return null;
        }

        #endregion

        #region Video Frame

        private void HandleVideoFrame(byte[] frame)
        {
            if (_receiver == null || frame == null || frame.Length == 0) return;

            var outBuf = ArrayPool<byte>.Shared.Rent(1 + frame.Length);
            outBuf[0] = (byte)HeaderType.VIDEO;
            frame.AsSpan().CopyTo(outBuf.AsSpan(1));

            Interlocked.Increment(ref _videoFrameCounter);

            try { _receiver.OnVideoPacket(outBuf.AsSpan(0, frame.Length + 1).ToArray()); }
            catch (Exception ex) { _logger.LogError(ex, "❌ Failed to send video frame"); }
            finally { ArrayPool<byte>.Shared.Return(outBuf); }
        }

        #endregion

        #region Worker

        public void StartWorker()
        {
            if (_workerTask != null && !_workerTask.IsCompleted) return;

            _workerCts?.Cancel();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _workerTask = Task.Run(() =>
            {
                _logger.LogInformation("✅ AVHandler worker started");
                int processedCount = 0;
                DateTime lastLog = DateTime.Now;

                while (!token.IsCancellationRequested && !_ct.IsCancellationRequested)
                {
                    int batch = 5;
                    for (int i = 0; i < batch; i++)
                    {
                        if (!_queue.TryDequeue(out var pkt)) break;
                        try
                        {
                            ProcessSinglePacket(pkt);
                            processedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Error processing AV packet frame={Frame}", pkt.FrameIndex);
                        }
                    }

                    if (_queue.IsEmpty) Thread.Sleep(0);

                    var now = DateTime.Now;
                    if ((now - lastLog).TotalSeconds > 10)
                    {
                        _logger.LogDebug("📊 Worker processed {Count} packets, queue={Queue}", processedCount, _queue.Count);
                        lastLog = now;
                    }
                }

                _queue.Clear();
                _logger.LogDebug("AVHandler worker stopped, total processed={Count}", processedCount);
            }, token);
        }

        #endregion

        #region Stop & Stats

        public void Stop()
        {
            _workerCts?.Cancel();
            _queue.Clear();
            _waiting = false;
            ResetVideoReorderQueue();
            ResetHealthState();
        }

        public StreamPipelineStats GetAndResetStats()
        {
            (int videoReceived, int videoLost) = _videoStream?.ConsumeAndResetCounters() ?? (0, 0);
            (int audioReceived, int audioLost) = _audioStream?.ConsumeAndResetCounters() ?? (0, 0);
            (int fecAttempts, int fecSuccess, int fecFailures) = _videoStream?.ConsumeAndResetFecCounters() ?? (0, 0, 0);
            int pendingPackets = _queue.Count;

            double fecSuccessRate = fecAttempts > 0 ? (double)fecSuccess / fecAttempts : 0.0;

            return new StreamPipelineStats
            {
                VideoReceived = videoReceived,
                VideoLost = videoLost,
                AudioReceived = audioReceived,
                AudioLost = audioLost,
                PendingPackets = pendingPackets,
                FecAttempts = fecAttempts,
                FecSuccess = fecSuccess,
                FecFailures = fecFailures,
                FecSuccessRate = fecSuccessRate
            };
        }

        public StreamHealthSnapshot GetHealthSnapshot(bool resetDeltas = false)
        {
            lock (_healthLock)
            {
                var now = DateTime.UtcNow;
                while (_recentFrameStatuses.Count > 0 && now - _recentFrameStatuses.Peek().Timestamp > _healthWindow)
                    _recentFrameStatuses.Dequeue();
                while (_recentFrameIntervals.Count > 0 && now - _recentFrameIntervals.Peek().Timestamp > _healthWindow)
                {
                    var removed = _recentFrameIntervals.Dequeue();
                    _recentIntervalSumMs -= removed.IntervalMs;
                }
                if (_recentIntervalSumMs < 0)
                    _recentIntervalSumMs = 0;

                int recentSuccess = 0;
                int recentRecovered = 0;
                int recentFrozen = 0;
                int recentDropped = 0;
                DateTime oldest = DateTime.MaxValue;
                DateTime newest = DateTime.MinValue;

                foreach (var entry in _recentFrameStatuses)
                {
                    if (entry.Timestamp < oldest)
                        oldest = entry.Timestamp;
                    if (entry.Timestamp > newest)
                        newest = entry.Timestamp;

                    switch (entry.Status)
                    {
                        case FrameProcessStatus.Success:
                            recentSuccess++;
                            break;
                        case FrameProcessStatus.Recovered:
                            recentRecovered++;
                            break;
                        case FrameProcessStatus.Frozen:
                            recentFrozen++;
                            break;
                        case FrameProcessStatus.Dropped:
                            recentDropped++;
                            break;
                    }
                }

                if (_recentFrameStatuses.Count == 0)
                {
                    oldest = now;
                    newest = now;
                }

                double averageIntervalMs = _recentFrameIntervals.Count > 0
                    ? _recentIntervalSumMs / _recentFrameIntervals.Count
                    : 0;

                double recentFps = 0;
                if (averageIntervalMs > 0)
                {
                    recentFps = 1000.0 / averageIntervalMs;
                }
                else if (_recentFrameStatuses.Count > 1 && newest > oldest)
                {
                    double spanSeconds = Math.Max(0.001, (newest - oldest).TotalSeconds);
                    recentFps = _recentFrameStatuses.Count / spanSeconds;
                }

                int deltaRecovered = _deltaRecoveredFrames;
                int deltaFrozen = _deltaFrozenFrames;
                int deltaDropped = _deltaDroppedFrames;

                if (resetDeltas)
                {
                    _deltaRecoveredFrames = 0;
                    _deltaFrozenFrames = 0;
                    _deltaDroppedFrames = 0;
                }

                return new StreamHealthSnapshot
                {
                    Timestamp = _lastHealthTimestamp,
                    LastStatus = _lastFrameStatus,
                    Message = _lastHealthMessage,
                    ConsecutiveFailures = _consecutiveVideoFailures,
                    TotalRecoveredFrames = _totalRecoveredFrames,
                    TotalFrozenFrames = _totalFrozenFrames,
                    TotalDroppedFrames = _totalDroppedFrames,
                    DeltaRecoveredFrames = deltaRecovered,
                    DeltaFrozenFrames = deltaFrozen,
                    DeltaDroppedFrames = deltaDropped,
                    RecentWindowSeconds = (int)_healthWindow.TotalSeconds,
                    RecentSuccessFrames = recentSuccess,
                    RecentRecoveredFrames = recentRecovered,
                    RecentFrozenFrames = recentFrozen,
                    RecentDroppedFrames = recentDropped,
                    RecentFps = recentFps,
                    AverageFrameIntervalMs = averageIntervalMs,
                    LastFrameTimestampUtc = _lastFrameTimestampUtc
                };
            }
        }

        #endregion

        private void HandleVideoFrameResult(FrameProcessInfo info)
        {
            StreamHealthEvent healthEvent;
            lock (_healthLock)
            {
                var now = DateTime.UtcNow;
                _lastFrameStatus = info.Status;
                _lastHealthMessage = info.Reason;
                _lastHealthTimestamp = now;

                switch (info.Status)
                {
                    case FrameProcessStatus.Success:
                        _consecutiveVideoFailures = 0;
                        break;
                    case FrameProcessStatus.Recovered:
                        _totalRecoveredFrames++;
                        _deltaRecoveredFrames++;
                        _consecutiveVideoFailures = 0;
                        break;
                    case FrameProcessStatus.Frozen:
                        _totalFrozenFrames++;
                        _deltaFrozenFrames++;
                        _consecutiveVideoFailures++;
                        break;
                    case FrameProcessStatus.Dropped:
                        _totalDroppedFrames++;
                        _deltaDroppedFrames++;
                        _consecutiveVideoFailures++;
                        break;
                }

                _recentFrameStatuses.Enqueue((now, info.Status));
                while (_recentFrameStatuses.Count > 0 && now - _recentFrameStatuses.Peek().Timestamp > _healthWindow)
                    _recentFrameStatuses.Dequeue();

                if (_lastFrameTimestampUtc != DateTime.MinValue)
                {
                    double intervalMs = (now - _lastFrameTimestampUtc).TotalMilliseconds;
                    if (intervalMs > 0 && intervalMs < 5000)
                    {
                        _recentFrameIntervals.Enqueue((now, intervalMs));
                        _recentIntervalSumMs += intervalMs;
                        while (_recentFrameIntervals.Count > 0 && now - _recentFrameIntervals.Peek().Timestamp > _healthWindow)
                        {
                            var removed = _recentFrameIntervals.Dequeue();
                            _recentIntervalSumMs -= removed.IntervalMs;
                        }
                    }
                }
                _lastFrameTimestampUtc = now;

                healthEvent = new StreamHealthEvent(
                    now,
                    info.FrameIndex,
                    info.Status,
                    _consecutiveVideoFailures,
                    info.Reason,
                    info.ReusedLastFrame,
                    info.RecoveredByFec);
            }

            _healthCallback?.Invoke(healthEvent);
        }
    }
}
