using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Quality;
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
        private const int QueueWarningThreshold = 2000; // ✅ 队列警告阈值（提前触发清理）
        private const int QueueCriticalThreshold = 3500; // ✅ 队列严重阈值（强制清理）
        private volatile bool _waiting = false;

        private const int DirectProcessThreshold = 10;
        private int _directProcessCount = 0;
        private DateTime _lastQueueCleanupTime = DateTime.MinValue; // ✅ 上次队列清理时间
        private const int QueueCleanupIntervalMs = 5000; // ✅ 队列清理间隔（5秒）

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
        private AdaptiveStreamManager? _adaptiveStreamManager;
        private Action<VideoProfile>? _profileSwitchCallback;
        private FrameProcessStatus _lastFrameStatus = FrameProcessStatus.Success;
        private string? _lastHealthMessage;
        private int _consecutiveVideoFailures = 0;
        private int _totalRecoveredFrames = 0;
        private int _totalFrozenFrames = 0;
        private int _totalDroppedFrames = 0;
        private int _consecutiveTimeoutCount = 0; // ✅ 连续超时计数（用于检测持续超时）
        private int _consecutiveFullDropCount = 0; // ✅ 连续满载丢弃计数（用于检测持续满载）
        private DateTime _lastTimeoutTime = DateTime.MinValue; // ✅ 最后一次超时时间
        private DateTime _lastFullDropTime = DateTime.MinValue; // ✅ 最后一次满载丢弃时间
        private DateTime _lastRecoveryTime = DateTime.MinValue; // ✅ 最后一次恢复时间（避免频繁恢复）
        private const int MAX_CONSECUTIVE_TIMEOUT = 3; // ✅ 最大连续超时次数（降低阈值，更快触发恢复）
        private const int MAX_CONSECUTIVE_FULL_DROPS = 10; // ✅ 最大连续满载丢弃次数（降低阈值，更快触发恢复）
        private static readonly TimeSpan TIMEOUT_WINDOW = TimeSpan.FromSeconds(1); // ✅ 超时窗口（1秒内的超时才算连续）
        private static readonly TimeSpan FULL_DROP_WINDOW = TimeSpan.FromSeconds(2); // ✅ 满载丢弃窗口（2秒内的丢弃才算连续）
        private static readonly TimeSpan RECOVERY_COOLDOWN = TimeSpan.FromSeconds(1); // ✅ 恢复冷却时间（缩短冷却时间，更快响应）
        private Func<Task>? _requestKeyframeCallback; // ✅ 请求关键帧回调（用于超时恢复）
        private readonly object _timeoutLock = new object(); // ✅ 超时锁（避免并发问题）
        private int _deltaRecoveredFrames = 0;
        private int _deltaFrozenFrames = 0;
        private int _deltaDroppedFrames = 0;
        private DateTime _lastHealthTimestamp = DateTime.MinValue;
        private DateTime _lastFrameTimestampUtc = DateTime.MinValue;
        private int _lastSuccessFrameIndex = -1; // ✅ 跟踪最后成功的帧索引，用于检测重复帧
        private DateTime _lastSuccessFrameTimestamp = DateTime.MinValue; // ✅ 最后成功帧的时间戳
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
                if (_videoReorderQueue == null)
                {
                    _logger.LogWarning("⚠️ Video reorder queue is null, cannot process video packet");
                    return;
                }
                _videoReorderQueue?.Push(packet);
                // ✅ 关键修复：每次推入视频包后触发一次超时扫描，避免因缺失期望序列导致的长期阻塞
                _videoReorderQueue?.Flush(false);
            }
            else
            {
                HandleOrderedPacket(packet);
            }
        }

        private void ProcessSinglePacket(AVPacket packet)
        {
            // ✅ 检测并处理 adaptive_stream_index 切换（参考 chiaki-ng）
            if (packet.Type == HeaderType.VIDEO && _adaptiveStreamManager != null)
            {
                var (switched, newProfile, needUpdateHeader) = _adaptiveStreamManager.CheckAndHandleSwitch(packet, _profileSwitchCallback);
                
                if (switched && needUpdateHeader && newProfile != null && _videoStream != null)
                {
                    // 更新 AVStream 的 header（参考 chiaki-ng: video_receiver_stream_info）
                    try
                    {
                        _videoStream.UpdateHeader(newProfile.HeaderWithPadding);
                        _logger.LogDebug("✅ AVStream header 已更新为 Profile[{Index}]: {Width}x{Height}", 
                            newProfile.Index, newProfile.Width, newProfile.Height);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ 更新 AVStream header 失败");
                    }
                }
            }

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
                // ✅ 音频包处理：如果 _audioStream 为 null，记录警告但不阻塞
                if (_audioStream == null)
                {
                    _logger.LogWarning("⚠️ AudioStream is null, cannot process audio packet: frame={Frame}, unit={Unit}",
                        packet.FrameIndex, packet.UnitIndex);
                    return;
                }
                _audioStream.Handle(packet, decrypted);
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

        /// <summary>
        /// 设置自适应流管理器（用于检测 profile 切换）
        /// </summary>
        public void SetAdaptiveStreamManager(AdaptiveStreamManager? manager, Action<VideoProfile>? onProfileSwitch = null)
        {
            _adaptiveStreamManager = manager;
            _profileSwitchCallback = onProfileSwitch;
        }

        /// <summary>
        /// 设置请求关键帧回调（用于超时恢复）
        /// </summary>
        public void SetRequestKeyframeCallback(Func<Task>? callback)
        {
            _requestKeyframeCallback = callback;
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
                _lastSuccessFrameIndex = -1;
                _lastSuccessFrameTimestamp = DateTime.MinValue;
                _recentFrameStatuses.Clear();
                _recentFrameIntervals.Clear();
                _recentIntervalSumMs = 0;
                _consecutiveTimeoutCount = 0; // ✅ 重置连续超时计数
                _lastTimeoutTime = DateTime.MinValue; // ✅ 重置超时时间
                _lastRecoveryTime = DateTime.MinValue; // ✅ 重置恢复时间
            }
        }

        private void ResetVideoReorderQueue()
        {
            // ✅ 如果已有重排序队列，先重置状态（允许重新初始化）
            if (_videoReorderQueue != null)
            {
                _videoReorderQueue.Reset();
            }
            
            // ✅ 使用 BEGIN 策略，丢弃最旧的包而不是新包（更适合视频流）
            // ✅ 增大缓冲区大小以应对网络抖动和乱序（参考 chiaki-ng）
            // ✅ 增加超时时间（从 50ms 增加到 150ms），减少网络抖动时的超时
            _videoReorderQueue = new ReorderQueue<AVPacket>(
                _logger,
                pkt => (uint)pkt.Index,
                HandleOrderedPacket,
                dropCallback: (droppedPacket) =>
                {
                    // ✅ 记录丢弃的视频包，帮助诊断
                    _logger.LogWarning("⚠️ Video packet dropped in reorder queue: seq={Seq}, frame={Frame}, type={Type}",
                        droppedPacket.Index, droppedPacket.FrameIndex, droppedPacket.Type);
                },
				sizeStart: 64,   // 初始大小
				sizeMin: 32,     // 最小大小
				sizeMax: 256,    // 下调最大容量，避免长时间堆积导致突发输出
				timeoutMs: 200,  // 下调超时时间，加快过期丢弃，减少迟到帧造成的抖动
                dropStrategy: ReorderQueueDropStrategy.Begin, // 使用 BEGIN 策略，避免丢弃新包
                timeoutCallback: OnReorderQueueTimeout); // ✅ 设置超时回调，用于检测持续超时
                
            // ✅ 设置满载丢弃回调（用于检测持续满载）
            _videoReorderQueue.SetTimeoutCallback(OnReorderQueueFullDrop);
            
            // ✅ 重置 _videoReorderQueueExpected = 0，让新队列根据第一个到达的包自动初始化
            // 这样可以避免序列号不匹配，确保重置后能正确处理后续的包（包括关键帧包）
            // ReorderQueue 会在首次 Push 时自动将 _nextExpectedSeq 设置为第一个包的序列号
            // 如果保持 _videoReorderQueueExpected 不变，而下一个包的序列号较小（如重置后的旧包），
            // 可能会导致新队列认为这个包是"过期包"而丢弃
            // 因此，重置 _videoReorderQueueExpected = 0，让新队列从第一个到达的包开始
            _videoReorderQueueExpected = 0; // ✅ 重置期望序列号，让队列从下一个包开始
            _consecutiveTimeoutCount = 0; // ✅ 重置连续超时计数
            _consecutiveFullDropCount = 0; // ✅ 重置连续满载丢弃计数
            _lastTimeoutTime = DateTime.MinValue; // ✅ 重置超时时间
            _lastFullDropTime = DateTime.MinValue; // ✅ 重置满载丢弃时间
			_waiting = true; // ✅ 重置后等待下一个 unit_index=0（通常为关键帧起点），避免在 P 帧上继续输出造成重复/抖动
			_logger.LogWarning("🔄 Video reorder queue reset: cleared buffered packets, will wait for unit_index=0 (expect keyframe) to resume");
        }

        /// <summary>
        /// 处理重排序队列超时和持续满载（用于检测持续超时/满载并触发恢复）
        /// ⚠️ 注意：只影响视频处理，不影响音频处理
        /// </summary>
        private void OnReorderQueueTimeout()
        {
            var now = DateTime.UtcNow;
            bool shouldRecover = false;
            string reason = "";
            
            lock (_timeoutLock)
            {
                // ✅ 检查是否在超时窗口内连续超时
                if (_lastTimeoutTime != DateTime.MinValue && (now - _lastTimeoutTime) < TIMEOUT_WINDOW)
                {
                    _consecutiveTimeoutCount++;
                }
                else
                {
                    // ✅ 超时间隔较长，重置计数
                    _consecutiveTimeoutCount = 1;
                }
                _lastTimeoutTime = now;
                
                // ✅ 检查是否在满载丢弃窗口内连续满载
                if (_lastFullDropTime != DateTime.MinValue && (now - _lastFullDropTime) < FULL_DROP_WINDOW)
                {
                    _consecutiveFullDropCount++;
                }
                else
                {
                    // ✅ 满载丢弃间隔较长，重置计数
                    _consecutiveFullDropCount = 1;
                }
                _lastFullDropTime = now;

                // ✅ 检查是否需要触发恢复策略
                if (_consecutiveFullDropCount >= MAX_CONSECUTIVE_FULL_DROPS)
                {
                    // ✅ 持续满载：立即触发恢复（优先级更高）
                    shouldRecover = true;
                    reason = $"持续满载（连续 {_consecutiveFullDropCount} 次，窗口 {FULL_DROP_WINDOW.TotalSeconds}s）";
                }
                else if (_consecutiveTimeoutCount >= MAX_CONSECUTIVE_TIMEOUT)
                {
                    // ✅ 持续超时：触发恢复
                    shouldRecover = true;
                    reason = $"持续超时（连续 {_consecutiveTimeoutCount} 次，窗口 {TIMEOUT_WINDOW.TotalSeconds}s）";
                }

                // ✅ 触发恢复策略（只影响视频，不影响音频）
                if (shouldRecover)
                {
                    // ✅ 检查恢复冷却时间（避免频繁恢复）
                    if (_lastRecoveryTime == DateTime.MinValue || (now - _lastRecoveryTime) >= RECOVERY_COOLDOWN)
                    {
                        _logger.LogWarning("⚠️ 检测到 {Reason}，触发恢复策略：重置视频重排序队列并请求关键帧（不影响音频）",
                            reason);
                        
                        // ✅ 重置重排序队列（只影响视频，不影响音频）
                        // 注意：这会清空所有积压的视频包，重置期望序列号
                        ResetVideoReorderQueue();
                        
                        // ✅ 延迟请求关键帧，确保重置完成后再请求（避免关键帧包在重置前到达）
                        // 注意：关键帧请求的冷却检查在 RPStreamV2 中处理
                        if (_requestKeyframeCallback != null)
                        {
                            // ✅ 使用 Task.Run 异步执行，避免阻塞
                            _ = Task.Run(async () =>
                            {
                                // ✅ 延迟 100ms，确保重置完成
                                await Task.Delay(100);
                                await _requestKeyframeCallback();
                            });
                        }
                        
                        _lastRecoveryTime = now;
                        _consecutiveTimeoutCount = 0; // ✅ 重置连续超时计数
                        _consecutiveFullDropCount = 0; // ✅ 重置连续满载丢弃计数
                        _lastTimeoutTime = DateTime.MinValue; // ✅ 重置超时时间
                        _lastFullDropTime = DateTime.MinValue; // ✅ 重置满载丢弃时间
                    }
                    else
                    {
                        var remaining = RECOVERY_COOLDOWN - (now - _lastRecoveryTime);
                        _logger.LogDebug("恢复冷却时间未到（剩余 {Remaining}s），跳过恢复（{Reason}）", 
                            remaining.TotalSeconds, reason);
                    }
                }
            }
        }
        
        /// <summary>
        /// 通知满载丢弃（用于检测持续满载并触发恢复）
        /// ⚠️ 注意：这个方法会在重排序队列检测到持续满载时被调用
        /// </summary>
        private void OnReorderQueueFullDrop()
        {
            var now = DateTime.UtcNow;
            lock (_timeoutLock)
            {
                // ✅ 检查恢复冷却时间（避免频繁恢复）
                if (_lastRecoveryTime == DateTime.MinValue || (now - _lastRecoveryTime) >= RECOVERY_COOLDOWN)
                {
                    _logger.LogWarning("⚠️ 检测到持续缓冲区满载，触发恢复策略：重置视频重排序队列并请求关键帧（不影响音频）");
                    
                    // ✅ 重置重排序队列（只影响视频，不影响音频）
                    // 注意：这会清空所有积压的视频包，重置期望序列号
                    ResetVideoReorderQueue();
                    
                    // ✅ 延迟请求关键帧，确保重置完成后再请求（避免关键帧包在重置前到达）
                    // 注意：关键帧请求的冷却检查在 RPStreamV2 中处理
                    if (_requestKeyframeCallback != null)
                    {
                        // ✅ 使用 Task.Run 异步执行，避免阻塞
                        _ = Task.Run(async () =>
                        {
                            // ✅ 延迟 100ms，确保重置完成
                            await Task.Delay(100);
                            await _requestKeyframeCallback();
                        });
                    }
                    
                    _lastRecoveryTime = now;
                    _consecutiveFullDropCount = 0; // ✅ 重置连续满载丢弃计数
                    _lastFullDropTime = DateTime.MinValue; // ✅ 重置满载丢弃时间
                    _consecutiveTimeoutCount = 0; // ✅ 重置连续超时计数
                    _lastTimeoutTime = DateTime.MinValue; // ✅ 重置超时时间
                }
                else
                {
                    var remaining = RECOVERY_COOLDOWN - (now - _lastRecoveryTime);
                    _logger.LogDebug("恢复冷却时间未到（剩余 {Remaining}s），跳过恢复", 
                        remaining.TotalSeconds);
                }
            }
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

            // ✅ 队列溢出处理：分阶段清理，避免延迟累积
            int queueCount = _queue.Count;
            bool shouldCleanup = false;
            string cleanupReason = "";
            
            // 阶段1: 严重阈值（强制清理）
            if (queueCount >= MaxQueueSize)
            {
                shouldCleanup = true;
                cleanupReason = $"队列溢出 (count={queueCount} >= max={MaxQueueSize})";
            }
            // 阶段2: 严重阈值（积极清理）
            else if (queueCount >= QueueCriticalThreshold)
            {
                var now = DateTime.UtcNow;
                // 如果距离上次清理超过间隔，执行清理
                if (_lastQueueCleanupTime == DateTime.MinValue || 
                    (now - _lastQueueCleanupTime).TotalMilliseconds >= QueueCleanupIntervalMs)
                {
                    shouldCleanup = true;
                    cleanupReason = $"队列严重积压 (count={queueCount} >= critical={QueueCriticalThreshold})";
                }
            }
            // 阶段3: 警告阈值（预防性清理，保留更多数据）
            else if (queueCount >= QueueWarningThreshold)
            {
                var now = DateTime.UtcNow;
                // 更长的清理间隔，避免过度清理
                if (_lastQueueCleanupTime == DateTime.MinValue || 
                    (now - _lastQueueCleanupTime).TotalMilliseconds >= QueueCleanupIntervalMs * 2)
                {
                    shouldCleanup = true;
                    cleanupReason = $"队列积压 (count={queueCount} >= warning={QueueWarningThreshold})";
                }
            }
            
            if (shouldCleanup)
            {
                // ✅ 根据队列大小决定清理比例：队列越大，清理越多
                int packetsToRemove = queueCount >= MaxQueueSize 
                    ? queueCount - QueueWarningThreshold // 溢出时清理到警告阈值
                    : queueCount >= QueueCriticalThreshold
                        ? (int)(queueCount * 0.5) // 严重时清理50%
                        : (int)(queueCount * 0.3); // 警告时清理30%
                
                // ✅ 只清空视频包，保留音频包
                var tempQueue = new Queue<AVPacket>();
                int videoPacketsRemoved = 0;
                int audioPacketsKept = 0;
                int removed = 0;
                
                while (_queue.TryDequeue(out var queuedPacket) && removed < packetsToRemove)
                {
                    if (queuedPacket.Type == HeaderType.VIDEO)
                    {
                        videoPacketsRemoved++;
                        removed++;
                        // 丢弃视频包
                    }
                    else
                    {
                        // ✅ 保留音频包
                        tempQueue.Enqueue(queuedPacket);
                        audioPacketsKept++;
                        removed++;
                    }
                }
                
                // ✅ 将保留的音频包重新放入队列
                while (tempQueue.TryDequeue(out var audioPacket))
                {
                    _queue.Enqueue(audioPacket);
                }
                
                if (videoPacketsRemoved > 0)
                {
                    _lastQueueCleanupTime = DateTime.UtcNow;
                    _logger.LogWarning("⚠️ AV queue cleanup: {Reason}, removed {VideoCount} video packets, kept {AudioCount} audio packets, resetting reorder queue",
                        cleanupReason, videoPacketsRemoved, audioPacketsKept);
                    ResetVideoReorderQueue();
                    // ✅ 重置后不设置 _waiting = true，因为序列号已经不同步，等待 unit_index=0 可能永远等不到
                    // 重置后的队列会自动接受下一个到达的包作为新的起始点
                    _waiting = false;
                }
            }

            if (_waiting)
            {
                // ✅ 音频包不受 _waiting 状态影响，直接处理
                if (!isVideo)
                {
                    // 音频包继续处理，不等待 unit_index=0
                }
                else if (packet.UnitIndex != 0)
                {
                    // 视频包且不是 unit_index=0，等待
                    return;
                }
                else
                {
                    // 视频包且是 unit_index=0，重置等待状态
                    _waiting = false;
                }
            }

            // ✅ 音频包优先直接处理（避免队列延迟）
            // 注意：即使 _cipher 为 null，音频包也应该处理（只是不解密）
            if (!isVideo)
            {
                try
                {
                    // ✅ 音频包直接处理，不依赖 _cipher 状态
                    // 如果 _cipher 为 null，DecryptPacket 会返回原始数据
                    ProcessSinglePacket(packet);
                    Interlocked.Increment(ref _directProcessCount);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Audio direct processing failed, enqueue instead: {Error}", ex.Message);
                    // 继续执行，让音频包入队处理
                }
            }

            // ✅ 如果队列较小，优先直接处理（减少延迟）
            // 注意：即使 _cipher 为 null，也应该处理包（DecryptPacket 会处理）
            if (_queue.Count < DirectProcessThreshold)
            {
                try
                {
                    ProcessSinglePacket(packet);
                    Interlocked.Increment(ref _directProcessCount);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Direct processing failed, enqueue instead: {Error}", ex.Message);
                    // 继续执行，让包入队处理
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
                DateTime lastWarningLog = DateTime.MinValue;

                while (!token.IsCancellationRequested && !_ct.IsCancellationRequested)
                {
                    int queueCount = _queue.Count;
                    
                    // ✅ 动态调整 batch 大小：队列越大，batch 越大，加快处理速度
                    int batch = queueCount > QueueCriticalThreshold
                        ? 100  // 严重积压时，增大 batch
                        : queueCount > QueueWarningThreshold
                            ? 75   // 警告时，中等 batch
                            : 50;  // 正常时，标准 batch
                    
                    int processedInBatch = 0;
                    
                    for (int i = 0; i < batch; i++)
                    {
                        if (!_queue.TryDequeue(out var pkt)) break;
                        try
                        {
                            ProcessSinglePacket(pkt);
                            processedCount++;
                            processedInBatch++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Error processing AV packet frame={Frame}", pkt.FrameIndex);
                        }
                    }

                    // ✅ 定期触发重排队列的超时检查（即使当前批次没有触发），对齐 chiaki-ng 的周期性扫描
                    // 避免因丢包/乱序导致的“等待缺失项”长期卡住从而引发冻结
                    _videoReorderQueue?.Flush(false);

                    // ✅ 改进等待策略：
                    // - 如果队列为空，短暂 sleep（1ms）以避免 CPU 空转
                    // - 如果队列不为空，根据队列大小决定 sleep 时间（队列大时不 sleep）
                    if (_queue.IsEmpty)
                    {
                        Thread.Sleep(1);
                    }
                    else if (queueCount > QueueWarningThreshold)
                    {
                        // 队列积压时，不 sleep，继续处理
                        // 可以添加 yield 让出 CPU，但保持低延迟
                        Thread.Yield();
                    }
                    // 如果队列较小，继续处理（不 sleep），提高处理速度

                    var now = DateTime.Now;
                    if ((now - lastLog).TotalSeconds > 10)
                    {
                        _logger.LogDebug("📊 Worker processed {Count} packets (batch={Batch}, queue={Queue})", 
                            processedCount, processedInBatch, _queue.Count);
                        lastLog = now;
                    }
                    
                    // ✅ 如果队列持续积压，记录警告（每秒最多记录一次）
                    if (queueCount > QueueWarningThreshold)
                    {
                        if (lastWarningLog == DateTime.MinValue || 
                            (now - lastWarningLog).TotalSeconds >= 1)
                        {
                            _logger.LogWarning("⚠️ Worker queue backlog: {Count} packets (warning={Warning}, critical={Critical})", 
                                queueCount, QueueWarningThreshold, QueueCriticalThreshold);
                            lastWarningLog = now;
                        }
                    }
                    else
                    {
                        lastWarningLog = DateTime.MinValue; // 重置警告时间
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
            (int videoReceived, int videoLost, int videoTimeoutDropped) = _videoStream?.ConsumeAndResetCounters() ?? (0, 0, 0);
            (int audioReceived, int audioLost, int audioTimeoutDropped) = _audioStream?.ConsumeAndResetCounters() ?? (0, 0, 0);
            (int fecAttempts, int fecSuccess, int fecFailures) = _videoStream?.ConsumeAndResetFecCounters() ?? (0, 0, 0);
            int pendingPackets = _queue.Count;

            double fecSuccessRate = fecAttempts > 0 ? (double)fecSuccess / fecAttempts : 0.0;

            return new StreamPipelineStats
            {
                VideoReceived = videoReceived,
                VideoLost = videoLost,
                VideoTimeoutDropped = videoTimeoutDropped,
                AudioReceived = audioReceived,
                AudioLost = audioLost,
                AudioTimeoutDropped = audioTimeoutDropped,
                PendingPackets = pendingPackets,
                FecAttempts = fecAttempts,
                FecSuccess = fecSuccess,
                FecFailures = fecFailures,
                FecSuccessRate = fecSuccessRate
            };
        }

        public StreamHealthSnapshot GetHealthSnapshot(bool resetDeltas = false, bool resetStreamStats = false)
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

                // ✅ 检测长时间没有新帧的情况（画面冻结）
                // 如果超过 3 秒没有新帧，应该认为画面已经冻结
                // 或者如果最近窗口内成功帧很少（可能是重复帧或黑帧），也应该标记为冻结
                const double STALL_THRESHOLD_SECONDS = 3.0;
                const double FPS_STALL_THRESHOLD = 1.0; // 如果 FPS < 1，认为画面冻结
                FrameProcessStatus finalStatus = _lastFrameStatus;
                string? finalMessage = _lastHealthMessage;
                
                if (_lastFrameTimestampUtc != DateTime.MinValue)
                {
                    var elapsedSinceLastFrame = (now - _lastFrameTimestampUtc).TotalSeconds;
                    
                    // ✅ 情况1: 长时间没有新帧（超过阈值）
                    if (elapsedSinceLastFrame > STALL_THRESHOLD_SECONDS)
                    {
                        // 长时间没有新帧，标记为冻结
                        finalStatus = FrameProcessStatus.Frozen;
                        finalMessage = $"画面冻结（{elapsedSinceLastFrame:F1}秒无新帧）";
                        
                        // ✅ 如果长时间没有新帧，增加冻结帧计数
                        if (_recentFrameStatuses.Count > 0)
                        {
                            // 只有在确实有历史帧的情况下才增加计数
                            // 避免在初始化时误报
                            recentFrozen++; // 增加当前窗口的冻结帧计数
                        }
                        
                        // ✅ 长时间没有新帧，FPS 应该为 0
                        recentFps = 0;
                        averageIntervalMs = 0;
                    }
                    // ✅ 情况2: 时间戳很新，但 FPS 异常低（可能是重复帧或黑帧）
                    else if (recentFps < FPS_STALL_THRESHOLD && _recentFrameStatuses.Count > 0)
                    {
                        // FPS 异常低，可能是画面冻结（虽然有事件，但实际没有新帧输出）
                        finalStatus = FrameProcessStatus.Frozen;
                        finalMessage = $"画面冻结（FPS={recentFps:F2}，可能重复帧或黑帧）";
                        
                        // 增加冻结帧计数
                        recentFrozen++;
                        
                        // FPS 已经很低，不需要再调整
                    }
                    // ✅ 情况3: 时间戳很新，但最近窗口内成功帧很少（可能是大部分帧都失败了）
                    else if (recentSuccess == 0 && _recentFrameStatuses.Count > 10)
                    {
                        // 最近窗口内有大量帧，但成功帧为 0，说明画面可能已冻结
                        finalStatus = FrameProcessStatus.Frozen;
                        finalMessage = $"画面冻结（最近窗口内成功帧为 0，总帧数={_recentFrameStatuses.Count}）";
                        
                        // 增加冻结帧计数
                        recentFrozen++;
                    }
                    // ✅ 情况4: 时间戳很新，但帧索引长时间没有增长（可能是重复帧或黑帧）
                    else if (_lastSuccessFrameIndex >= 0 && _lastSuccessFrameTimestamp != DateTime.MinValue)
                    {
                        var elapsedSinceNewFrame = (now - _lastSuccessFrameTimestamp).TotalSeconds;
                        // 如果超过 2 秒没有新的帧索引，且最近 FPS 也偏低，可能画面已冻结（避免 FPS 正常时误判）
                        if (elapsedSinceNewFrame > 2.0 && recentFps > 0 && recentFps < 5.0)
                        {
                            finalStatus = FrameProcessStatus.Frozen;
                            finalMessage = $"画面冻结（{elapsedSinceNewFrame:F1}秒无新帧索引，可能重复帧或黑帧，FPS={recentFps:F2}）";
                            
                            // 增加冻结帧计数
                            recentFrozen++;
                        }
                    }
                }
                else if (_recentFrameStatuses.Count == 0)
                {
                    // 从未收到过帧，可能是初始化阶段或连接问题
                    finalMessage = "等待首帧";
                    recentFps = 0;
                    averageIntervalMs = 0;
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

                // ✅ 获取流统计和计算码率（参考 chiaki-ng: chiaki_stream_stats_bitrate）
                ulong totalFrames = 0;
                ulong totalBytes = 0;
                double measuredBitrateMbps = 0.0;
                int framesLostDelta = 0;
                int frameIndexPrev = -1;

                if (_videoStream != null)
                {
                    var stats = _videoStream.GetStreamStats();
                    
                    // 如果 resetStreamStats 为 true，获取并重置统计（参考 chiaki-ng: chiaki_stream_stats_reset）
                    if (resetStreamStats)
                    {
                        (totalFrames, totalBytes) = _videoStream.GetAndResetStreamStats();
                    }
                    else
                    {
                        (totalFrames, totalBytes) = stats.GetSnapshot();
                    }
                    
                    // 使用 recentFps 或默认 30fps 计算码率
                    // 参考 chiaki-ng: stream_connection->measured_bitrate = chiaki_stream_stats_bitrate(...) / 1000000.0
                    ulong framerate = recentFps > 0 ? (ulong)Math.Round(recentFps) : 30; // 默认 30fps
                    measuredBitrateMbps = stats.GetBitrateMbps(framerate);

                    // ✅ 获取并重置帧索引统计（frames_lost）
                    var (prev, lost) = _videoStream.ConsumeAndResetFrameIndexStats();
                    framesLostDelta = lost;
                    frameIndexPrev = prev;
                }

                return new StreamHealthSnapshot
                {
                    Timestamp = _lastHealthTimestamp,
                    LastStatus = finalStatus, // ✅ 使用检测后的最终状态
                    Message = finalMessage,   // ✅ 使用检测后的最终消息
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
                    LastFrameTimestampUtc = _lastFrameTimestampUtc,
                    TotalFrames = totalFrames,
                    TotalBytes = totalBytes,
                    MeasuredBitrateMbps = measuredBitrateMbps,
                    FramesLost = framesLostDelta,
                    FrameIndexPrev = frameIndexPrev
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
                        // ✅ 跟踪成功帧的索引，用于检测重复帧或黑帧
                        if (_lastSuccessFrameIndex < 0 || SequenceNumber.Less((ushort)_lastSuccessFrameIndex, (ushort)info.FrameIndex))
                        {
                            _lastSuccessFrameIndex = info.FrameIndex;
                            _lastSuccessFrameTimestamp = now;
                        }
                        _consecutiveVideoFailures = 0;
                        break;
                    case FrameProcessStatus.FecSuccess:
                        // 视为恢复成功的一种
                        if (_lastSuccessFrameIndex < 0 || SequenceNumber.Less((ushort)_lastSuccessFrameIndex, (ushort)info.FrameIndex))
                        {
                            _lastSuccessFrameIndex = info.FrameIndex;
                            _lastSuccessFrameTimestamp = now;
                        }
                        _totalRecoveredFrames++;
                        _deltaRecoveredFrames++;
                        _consecutiveVideoFailures = 0;
                        break;
                    case FrameProcessStatus.Recovered:
                        // ✅ 恢复的帧也认为是成功帧
                        if (_lastSuccessFrameIndex < 0 || SequenceNumber.Less((ushort)_lastSuccessFrameIndex, (ushort)info.FrameIndex))
                        {
                            _lastSuccessFrameIndex = info.FrameIndex;
                            _lastSuccessFrameTimestamp = now;
                        }
                        _totalRecoveredFrames++;
                        _deltaRecoveredFrames++;
                        _consecutiveVideoFailures = 0;
                        break;
                    case FrameProcessStatus.FecFailed:
                        _totalDroppedFrames++;
                        _deltaDroppedFrames++;
                        _consecutiveVideoFailures++;
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
