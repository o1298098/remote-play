using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Buffer;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// AV 处理器 V2
    /// 使用 FrameProcessor 和 VideoReceiver 分离关注点
    /// </summary>
    public sealed class AVHandler
    {
        private readonly ILogger<AVHandler> _logger;
        private readonly string _hostType;
        private StreamCipher? _cipher;
        private IAVReceiver? _receiver;

        private readonly ConcurrentQueue<AVPacket> _queue = new();
        private ReorderQueue<AVPacket>? _videoReorderQueue;
        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly CancellationToken _ct;

        private VideoReceiver? _videoReceiver;
        private AudioReceiver? _audioReceiver;

        private string? _detectedVideoCodec;
        private string? _detectedAudioCodec;
        private VideoProfile[]? _videoProfiles;
        
        // 回调
        private Action<int, int>? _videoCorruptCallback;
        private Action<int, int>? _audioCorruptCallback;
        private Action<StreamHealthEvent>? _healthCallback;
        private AdaptiveStreamManager? _adaptiveStreamManager;
        private Action<VideoProfile, VideoProfile?>? _profileSwitchCallback;
        private Func<Task>? _requestKeyframeCallback;
        private Congestion.PacketStats? _packetStats;  // 包统计（用于拥塞控制）

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
            
            // 重置超时计数
            _consecutiveTimeouts = 0;
            _lastTimeoutTime = DateTime.MinValue;
        }

        #region Receiver / Cipher / Headers

        public void SetReceiver(IAVReceiver receiver)
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));

            var oldReceiver = _receiver;
            _receiver = receiver;

            if (oldReceiver != null)
                _logger.LogInformation("🔄 Switching receiver: {Old} -> {New}", oldReceiver.GetType().Name, receiver.GetType().Name);

            // 同步 stream info 和 codec
            if (_videoProfiles != null && _videoProfiles.Length > 0)
            {
                try
                {
                    var currentProfile = _videoProfiles[0];
                    receiver.OnStreamInfo(currentProfile.HeaderWithPadding, Array.Empty<byte>());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send stream info to new receiver");
                }
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
        }

        public void SetHeaders(byte[]? videoHeader, byte[]? audioHeader, ILoggerFactory loggerFactory)
        {
            // 从 AdaptiveStreamManager 获取 profiles
            VideoProfile[]? videoProfiles = null;
            if (_adaptiveStreamManager != null)
            {
                var profiles = _adaptiveStreamManager.GetAllProfiles();
                if (profiles.Count > 0)
                {
                    videoProfiles = profiles.ToArray();
                }
            }
            
            SetHeaders(videoHeader, audioHeader, videoProfiles, loggerFactory);
        }
        
        public void SetHeaders(byte[]? videoHeader, byte[]? audioHeader, VideoProfile[]? videoProfiles, ILoggerFactory loggerFactory)
        {
            if (_receiver == null)
            {
                _logger.LogWarning("⚠️ Cannot set headers: receiver is null");
                return;
            }

            ResetVideoReorderQueue();
            
            // 重置超时计数
            _consecutiveTimeouts = 0;
            _lastTimeoutTime = DateTime.MinValue;

            // 初始化 VideoReceiver
            _videoReceiver = new VideoReceiver(loggerFactory.CreateLogger<VideoReceiver>());
            // 设置 corrupt frame 回调
            _videoReceiver.SetCorruptFrameCallback(_videoCorruptCallback);
            if (videoProfiles != null && videoProfiles.Length > 0)
            {
                _videoProfiles = videoProfiles;
                _videoReceiver.SetStreamInfo(videoProfiles);
            }
            else if (videoHeader != null)
            {
                // 如果没有 profiles，创建一个默认的
                var defaultProfile = new VideoProfile(0, 1920, 1080, videoHeader);
                _videoProfiles = new[] { defaultProfile };
                _videoReceiver.SetStreamInfo(_videoProfiles);
            }

            // 初始化 AudioReceiver
            _audioReceiver = new AudioReceiver(loggerFactory.CreateLogger<AudioReceiver>());
            if (audioHeader != null)
            {
                _audioReceiver.SetHeader(audioHeader);
            }
            
            // ✅ 设置帧丢失回调：当检测到帧丢失时，通知 receiver 重置解码器
            _audioReceiver.SetFrameLossCallback((lostFrames) =>
            {
                if (_receiver is WebRTCReceiver webrtcReceiver)
                {
                    try
                    {
                        webrtcReceiver.ResetAudioDecoder(lostFrames);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Failed to reset audio decoder");
                    }
                }
            });

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
            try
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
                    _videoReorderQueue?.Flush(false);
                }
                else
                {
                    HandleOrderedPacket(packet);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception in AddPacket, len={Len}", msg.Length);
            }
        }

        private void ProcessSinglePacket(AVPacket packet)
        {
            // ✅ 推送序列号统计（类似 chiaki 的 chiaki_packet_stats_push_seq）
            // 使用 FrameIndex 作为序列号（音频和视频都使用）
            _packetStats?.PushSeq(packet.FrameIndex);
            
            // 检测并处理 adaptive_stream_index 切换
            if (packet.Type == HeaderType.VIDEO && _adaptiveStreamManager != null)
            {
                var (switched, newProfile, needUpdateHeader) = _adaptiveStreamManager.CheckAndHandleSwitch(packet, _profileSwitchCallback);
                
                // ✅ 注意：VideoReceiver.ProcessPacket 已经会自动处理 profile 切换
                // 不需要在这里再次调用 SetStreamInfo，因为 profiles 数组本身没有变化
                // 只是当前使用的 profile index 变了，ProcessPacket 会检测并处理
                if (switched && needUpdateHeader && newProfile != null)
                {
                    _logger.LogDebug("Profile switched to {Index}, VideoReceiver will handle it in ProcessPacket", 
                        newProfile.Index);
                }
            }

            byte[] decrypted = DecryptPacket(packet);
            if (packet.Type == HeaderType.VIDEO)
            {
                if (_videoReceiver == null)
                {
                    _logger.LogError("❌ VideoReceiver null, frame={Frame}", packet.FrameIndex);
                    return;
                }

                _videoReceiver.ProcessPacket(packet, decrypted, (frame, recovered, success) =>
                {
                    var now = DateTime.UtcNow;
                    FrameProcessStatus status;
                    
                    if (success)
                    {
                        if (recovered)
                        {
                            status = FrameProcessStatus.Recovered;
                        }
                        else
                        {
                            status = FrameProcessStatus.Success;
                        }
                    }
                    else
                    {
                        status = FrameProcessStatus.Dropped;
                    }
                    
                    // 记录健康状态
                    RecordFrameStatus(status, now);
                    
                    // ✅ 关键修复：在宽限期内，即使success=false，如果recovered=true，也应该发送帧
                    // 这可以避免在帧丢失后，因为参考帧缺失导致完全没有画面输出
                    // VideoReceiver会在宽限期内将success设置为true，但为了保险起见，这里也检查recovered
                    if (_receiver != null && (success || recovered))
                    {
                        var packetData = new byte[1 + frame.Length];
                        packetData[0] = (byte)HeaderType.VIDEO;
                        Array.Copy(frame, 0, packetData, 1, frame.Length);
                        try
                        {
                            // ✅ 检测是否为IDR关键帧，优先发送
                            bool isIdrFrame = IsIdrFrame(frame);
                            if (isIdrFrame && _receiver is WebRTCReceiver webrtcReceiver)
                            {
                                // IDR帧优先发送
                                webrtcReceiver.OnVideoPacketPriority(packetData);
                            }
                            else
                            {
                                _receiver.OnVideoPacket(packetData);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ OnVideoPacket error");
                        }
                    }
                });
            }
            else
            {
                if (_audioReceiver == null)
                {
                    _logger.LogWarning("⚠️ AudioReceiver is null, cannot process audio packet");
                    return;
                }

                _audioReceiver.ProcessPacket(packet, decrypted, (frame) =>
                {
                    if (_receiver != null)
                    {
                        var packetData = new byte[1 + frame.Length];
                        packetData[0] = (byte)HeaderType.AUDIO;
                        Array.Copy(frame, 0, packetData, 1, frame.Length);
                        try
                        {
                            _receiver.OnAudioPacket(packetData);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ OnAudioPacket error");
                        }
                    }
                });
            }
        }

        private byte[] DecryptPacket(AVPacket packet)
        {
            var data = packet.Data;
            if (_cipher != null && data.Length > 0 && packet.KeyPos > 0)
            {
                try { data = _cipher.Decrypt(data, packet.KeyPos); }
                catch (Exception ex) { _logger.LogError(ex, "❌ Decrypt failed frame={Frame}, keyPos={KeyPos}", packet.FrameIndex, packet.KeyPos); }
            }
            return data;
        }

        #endregion

        #region Callbacks

        public void SetCorruptFrameCallbacks(Action<int, int>? videoCallback, Action<int, int>? audioCallback = null)
        {
            _videoCorruptCallback = videoCallback;
            _audioCorruptCallback = audioCallback;
            // 如果 VideoReceiver 已存在，更新其回调
            _videoReceiver?.SetCorruptFrameCallback(videoCallback);
        }

        public void SetStreamHealthCallback(Action<StreamHealthEvent>? healthCallback)
        {
            _healthCallback = healthCallback;
        }

        public void SetAdaptiveStreamManager(AdaptiveStreamManager? manager, Action<VideoProfile, VideoProfile?>? onProfileSwitch = null)
        {
            _adaptiveStreamManager = manager;
            _profileSwitchCallback = onProfileSwitch;
        }

        /// <summary>
        /// 设置包统计（用于拥塞控制）
        /// </summary>
        public void SetPacketStats(Congestion.PacketStats? packetStats)
        {
            _packetStats = packetStats;
            // 同时设置到 VideoReceiver
            _videoReceiver?.SetPacketStats(packetStats);
        }

        public void SetRequestKeyframeCallback(Func<Task>? callback)
        {
            _requestKeyframeCallback = callback;
        }

        #endregion

        #region Reorder Queue

        private void ResetVideoReorderQueue()
        {
            _videoReorderQueue = new ReorderQueue<AVPacket>(
                _logger,
                pkt => (uint)pkt.Index,
                HandleOrderedPacket,
                dropCallback: (droppedPacket) =>
                {
                    _logger.LogWarning("⚠️ Video packet dropped in reorder queue: seq={Seq}, frame={Frame}, unitIndex={UnitIndex}/{Total}",
                        droppedPacket.Index, droppedPacket.FrameIndex, droppedPacket.UnitIndex, droppedPacket.UnitsTotal);
                    
                    var now = DateTime.UtcNow;
                    if (_lastDropTime != DateTime.MinValue && 
                        (now - _lastDropTime).TotalMilliseconds > DROP_WINDOW_MS)
                    {
                        _consecutiveDrops = 0;
                        _firstDropTime = DateTime.MinValue;
                    }
                    
                    if (_consecutiveDrops == 0)
                    {
                        _firstDropTime = now;
                    }
                    
                    _consecutiveDrops++;
                    _lastDropTime = now;
                    
                    var dropDuration = _firstDropTime != DateTime.MinValue 
                        ? (now - _firstDropTime).TotalMilliseconds 
                        : 0;
                    
                    bool shouldReset = _consecutiveDrops >= MAX_CONSECUTIVE_DROPS ||
                                      (dropDuration >= MAX_DROP_DURATION_MS && _consecutiveDrops >= 10);
                    
                    if (shouldReset)
                    {
                        var reason = _consecutiveDrops >= MAX_CONSECUTIVE_DROPS 
                            ? $"{_consecutiveDrops} consecutive packets dropped" 
                            : $"Packets dropped for {dropDuration:F0}ms ({_consecutiveDrops} packets)";
                        
                        _logger.LogError("🚨 {Reason}, resetting ReorderQueue to recover video (last dropped packet: seq={LastSeq}, frame={LastFrame})", 
                            reason, droppedPacket.Index, droppedPacket.FrameIndex);
                        
                        var statsBeforeReset = _videoReorderQueue?.GetStats() ?? (0, 0, 0, 0);
                        _logger.LogWarning("ReorderQueue stats before reset: processed={Processed}, dropped={Dropped}, timeout={Timeout}, bufferSize={BufferSize}", 
                            statsBeforeReset.processed, statsBeforeReset.dropped, statsBeforeReset.timeoutDropped, statsBeforeReset.bufferSize);
                        
                        ResetVideoReorderQueue();
                        _consecutiveDrops = 0;
                        _lastDropTime = DateTime.MinValue;
                        _firstDropTime = DateTime.MinValue;
                        _consecutiveTimeouts = 0;
                        _lastTimeoutTime = DateTime.MinValue;
                        
                        if (_requestKeyframeCallback != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _requestKeyframeCallback();
                                    _logger.LogInformation("✅ Requested keyframe after reset to recover video");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "❌ Failed to request keyframe after reset");
                                }
                            });
                        }
                    }
                },
                maxBufferFrames: 192,
                maxGap: 96,
                timeoutMsBase: 6); // 注意：新实现会将超时限制在 4-12ms 范围内
        }

        private int _consecutiveTimeouts = 0;
        private DateTime _lastTimeoutTime = DateTime.MinValue;
        private const int MAX_CONSECUTIVE_TIMEOUTS = 3;
        private const int TIMEOUT_WINDOW_MS = 3000;
        
        private int _consecutiveDrops = 0;
        private DateTime _lastDropTime = DateTime.MinValue;
        private DateTime _firstDropTime = DateTime.MinValue;
        private const int MAX_CONSECUTIVE_DROPS = 12;
        private const int DROP_WINDOW_MS = 1000;
        private const int MAX_DROP_DURATION_MS = 1200;

        // 健康状态跟踪
        private readonly object _healthLock = new();
        private FrameProcessStatus _lastStatus = FrameProcessStatus.Success;
        private int _consecutiveFailures = 0;
        private int _totalRecoveredFrames = 0;
        private int _totalFrozenFrames = 0;
        private int _totalDroppedFrames = 0;
        private int _deltaRecoveredFrames = 0;
        private int _deltaFrozenFrames = 0;
        private int _deltaDroppedFrames = 0;
        
        // 最近窗口统计（用于计算 FPS）
        private readonly Queue<(DateTime timestamp, FrameProcessStatus status)> _recentFrames = new();
        private const int RECENT_WINDOW_SECONDS = 10;
        private DateTime _lastFrameTimestamp = DateTime.UtcNow;
        private readonly List<double> _frameIntervals = new(); // 用于计算平均帧间隔

        private void OnReorderQueueTimeout()
        {
            var now = DateTime.UtcNow;
            
            // 检查是否在时间窗口内
            if (_lastTimeoutTime != DateTime.MinValue && 
                (now - _lastTimeoutTime).TotalMilliseconds > TIMEOUT_WINDOW_MS)
            {
                // 超过时间窗口，重置计数
                _consecutiveTimeouts = 0;
            }

            _consecutiveTimeouts++;
            _lastTimeoutTime = now;

            // 如果连续超时次数超过阈值，请求关键帧恢复
            if (_consecutiveTimeouts >= MAX_CONSECUTIVE_TIMEOUTS)
            {
                _logger.LogWarning("⚠️ {Count} consecutive timeouts, requesting keyframe to recover video", _consecutiveTimeouts);
                
                // 重置计数，避免重复请求
                _consecutiveTimeouts = 0;
                _lastTimeoutTime = DateTime.MinValue;

                // 异步请求关键帧（不阻塞）
                if (_requestKeyframeCallback != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _requestKeyframeCallback();
                            _logger.LogInformation("✅ Requested keyframe to recover video");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Failed to request keyframe");
                        }
                    });
                }
                else
                {
                    _logger.LogWarning("⚠️ RequestKeyframeCallback not set, cannot request keyframe");
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

            if (_receiver == null)
                return;

            // 音频包优先直接处理
            if (!isVideo)
            {
                try
                {
                    ProcessSinglePacket(packet);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Audio direct processing failed, enqueue instead");
                }
            }

            // 如果队列较小，优先直接处理
            if (_queue.Count < 10)
            {
                try
                {
                    ProcessSinglePacket(packet);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Direct processing failed, enqueue instead");
                }
            }

            // ✅ 关键修复：当队列积压时，主动丢弃旧包，避免延迟累积
            int queueCount = _queue.Count;
            const int MAX_QUEUE_SIZE = 150; // 最大队列大小
            const int DROP_THRESHOLD = 100; // 超过此值开始丢弃旧包
            
            if (queueCount >= MAX_QUEUE_SIZE)
            {
                // 队列已满，丢弃最旧的包（丢弃到阈值以下）
                int dropCount = queueCount - DROP_THRESHOLD + 1;
                int dropped = 0;
                while (_queue.TryDequeue(out var _) && dropped < dropCount)
                {
                    dropped++;
                }
                _logger.LogWarning("🚨 Queue full ({QueueCount} packets), dropped {Dropped} old packets to reduce latency", 
                    queueCount, dropped);
            }
            else if (queueCount >= DROP_THRESHOLD)
            {
                // 队列接近满，丢弃最旧的包
                if (_queue.TryDequeue(out var _))
                {
                    _logger.LogDebug("⚠️ Queue backlog ({QueueCount} packets), dropped 1 old packet", queueCount);
                }
            }
            
            _queue.Enqueue(packet);
            queueCount = _queue.Count;

            // ✅ 当队列积压时，输出警告日志
            if (queueCount > 200)
            {
                _logger.LogError("🚨 Severe queue backlog: {QueueCount} packets pending", queueCount);
            }
            else if (queueCount > 100 && queueCount % 50 == 0) // 每50个包输出一次，避免日志过多
            {
                _logger.LogWarning("⚠️ Queue backlog: {QueueCount} packets pending", queueCount);
            }

            if (queueCount > 100 && (_workerTask == null || _workerTask.IsCompleted) && _cipher != null)
            {
                _logger.LogError("❌ Queue has {Size} packets but worker not running! Starting...", queueCount);
                StartWorker();
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
            // 从 profile header 检测 codec
            string? codec = null;
            if (_videoProfiles != null && _videoProfiles.Length > 0)
            {
                codec = DetectCodecFromHeader(_videoProfiles[0].Header);
            }

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
            int len = Math.Max(header.Length - 64, 0);
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

        #region Worker

        public void StartWorker()
        {
            if (_workerTask != null && !_workerTask.IsCompleted) return;

            _workerCts?.Cancel();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _workerTask = Task.Run(() =>
            {
                _logger.LogInformation("✅ AVHandler2 worker started");
                int processedCount = 0;
                DateTime lastQueueLogTime = DateTime.UtcNow;
                DateTime lastTimeoutCheckTime = DateTime.UtcNow;
                const int QUEUE_LOG_INTERVAL_SECONDS = 5;
                const int TIMEOUT_CHECK_INTERVAL_MS = 100;

                while (!token.IsCancellationRequested && !_ct.IsCancellationRequested)
                {
                    // ✅ 关键修复：定期检查 ReorderQueue 的超时，即使没有新包到达
                    var now = DateTime.UtcNow;
                    if ((now - lastTimeoutCheckTime).TotalMilliseconds >= TIMEOUT_CHECK_INTERVAL_MS)
                    {
                        _videoReorderQueue?.Flush(false); // 检查超时
                        lastTimeoutCheckTime = now;
                    }

                    int batch = 50;
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

                    // ✅ 定期输出队列积压状态（每5秒）
                    if ((now - lastQueueLogTime).TotalSeconds >= QUEUE_LOG_INTERVAL_SECONDS)
                    {
                        int queueCount = _queue.Count;
                        var videoReorderStats = _videoReorderQueue?.GetStats() ?? (0, 0, 0, 0);
                        
                        // ✅ 关键修复：如果队列持续积压，主动清理旧包
                        const int CLEANUP_THRESHOLD = 120;
                        if (queueCount > CLEANUP_THRESHOLD)
                        {
                            int dropCount = queueCount - CLEANUP_THRESHOLD;
                            int dropped = 0;
                            while (_queue.TryDequeue(out var _) && dropped < dropCount)
                            {
                                dropped++;
                            }
                            if (dropped > 0)
                            {
                                _logger.LogWarning("🧹 Persistent queue backlog, purged {Dropped} old packets (queue size: {Before} -> {After})", 
                                    dropped, queueCount, _queue.Count);
                            }
                        }
                        
                        // 根据队列大小选择日志级别
                        if (queueCount > 200)
                        {
                            _logger.LogError("🚨 Severe queue backlog: main queue={QueueCount}, video reorder queue: processed={Processed}, dropped={Dropped}, timeout={Timeout}, bufferSize={BufferSize}, worker processed={ProcessedCount}",
                                queueCount, videoReorderStats.processed, videoReorderStats.dropped, videoReorderStats.timeoutDropped, videoReorderStats.bufferSize, processedCount);
                        }
                        else if (queueCount > 100)
                        {
                            _logger.LogWarning("⚠️ Queue backlog: main queue={QueueCount}, video reorder queue: processed={Processed}, dropped={Dropped}, timeout={Timeout}, bufferSize={BufferSize}, worker processed={ProcessedCount}",
                                queueCount, videoReorderStats.processed, videoReorderStats.dropped, videoReorderStats.timeoutDropped, videoReorderStats.bufferSize, processedCount);
                        }
                        else if (queueCount > 0 || videoReorderStats.bufferSize > 0)
                        {
                            _logger.LogInformation("📊 Queue status: main queue={QueueCount}, video reorder queue: processed={Processed}, dropped={Dropped}, timeout={Timeout}, bufferSize={BufferSize}, worker processed={ProcessedCount}",
                                queueCount, videoReorderStats.processed, videoReorderStats.dropped, videoReorderStats.timeoutDropped, videoReorderStats.bufferSize, processedCount);
                        }
                        
                        lastQueueLogTime = now;
                    }

                    // ✅ 优化：使用 CancellationToken.WaitHandle 等待，避免阻塞线程池线程
                    if (_queue.IsEmpty)
                    {
                        // 使用 WaitHandle 等待，这样可以在等待时释放线程池线程
                        // 等待最多 10ms，但会在取消信号触发时立即返回
                        var waitHandle = token.WaitHandle;
                        var ctWaitHandle = _ct.WaitHandle;
                        var handles = new[] { waitHandle, ctWaitHandle };
                        
                        // WaitAny 返回第一个触发的句柄索引（0=token, 1=_ct），或 WaitTimeout (-1)
                        int result = WaitHandle.WaitAny(handles, TimeSpan.FromMilliseconds(10));
                        
                        // ✅ Bug 2 修复：WaitHandle.WaitAny 返回 WaitHandle.WaitTimeout (-1) 表示超时
                        // 只有当返回值是有效的句柄索引（0 或 1）时才表示取消信号触发
                        if (result != WaitHandle.WaitTimeout)
                        {
                            // 取消信号触发（result == 0 表示 token，result == 1 表示 _ct）
                            break;
                        }
                        // 如果 result == WaitHandle.WaitTimeout，表示超时，继续循环（这是预期行为）
                    }
                    else
                    {
                        Thread.Yield();
                    }
                    
                    // ✅ 优化：在每次循环开始时立即检查取消信号
                    if (token.IsCancellationRequested || _ct.IsCancellationRequested)
                    {
                        break;
                    }
                }

                _queue.Clear();
                _logger.LogInformation("✅ AVHandler2 worker exited");
            }, token);
        }

        #endregion

        #region Stop & Stats

        public void Stop()
        {

            _workerCts?.Cancel();
            _queue.Clear();
            ResetVideoReorderQueue();
            
            // 重置超时计数
            _consecutiveTimeouts = 0;
            _lastTimeoutTime = DateTime.MinValue;

            if (_workerTask != null && !_workerTask.IsCompleted)
            {
                try
                {
                    // ✅ 优化：减少等待时间，避免阻塞关闭流程
                    var timeoutTask = Task.Delay(200); // 从 500ms 减少到 200ms
                    var completedTask = Task.WhenAny(_workerTask, timeoutTask).GetAwaiter().GetResult();
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("⚠️ AVHandler2 worker exit timed out (200ms), continuing");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Error while waiting for AVHandler2 worker to exit");
                }
            }
        }

        public StreamPipelineStats GetAndResetStats()
        {
            // 注意：packet stats 现在由 PacketStats 统一管理（类似 chiaki）
            // 这里只返回其他统计信息，packet stats 通过 PacketStats.GetAndReset 获取
            
            return new StreamPipelineStats
            {
                VideoReceived = 0,  // 现在由 PacketStats 统一管理
                VideoLost = 0,      // 现在由 PacketStats 统一管理
                VideoTimeoutDropped = 0, // TODO: 如果需要，可以从ReorderQueue获取
                AudioReceived = 0,  // 现在由 PacketStats 统一管理
                AudioLost = 0,      // 现在由 PacketStats 统一管理
                AudioTimeoutDropped = 0,
                PendingPackets = _queue.Count,
                FecAttempts = 0, // TODO: 如果需要，可以从FrameProcessor获取
                FecSuccess = 0,
                FecFailures = 0,
                FecSuccessRate = 0.0
            };
        }

        /// <summary>
        /// 强制重置 ReorderQueue（用户主动触发，解决画面冻结）
        /// ✅ 公共方法：允许外部主动重置队列以恢复卡顿的视频流
        /// </summary>
        public void ForceResetReorderQueue()
        {
            _logger.LogWarning("🔄 User requested ReorderQueue reset");
            
            var statsBeforeReset = _videoReorderQueue?.GetStats() ?? (0, 0, 0, 0);
            _logger.LogWarning("ReorderQueue stats before reset: processed={Processed}, dropped={Dropped}, timeout={Timeout}, bufferSize={BufferSize}", 
                statsBeforeReset.processed, statsBeforeReset.dropped, statsBeforeReset.timeoutDropped, statsBeforeReset.bufferSize);
            
            // 重置队列
            ResetVideoReorderQueue();
            
            // 重置相关计数器
            _consecutiveDrops = 0;
            _lastDropTime = DateTime.MinValue;
            _firstDropTime = DateTime.MinValue;
            _consecutiveTimeouts = 0;
            _lastTimeoutTime = DateTime.MinValue;
            
            // 请求关键帧以恢复视频流
            if (_requestKeyframeCallback != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _requestKeyframeCallback();
                        _logger.LogInformation("✅ Requested keyframe after reset to recover video");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Failed to request keyframe after reset");
                    }
                });
            }
        }

        private void RecordFrameStatus(FrameProcessStatus status, DateTime timestamp)
        {
            lock (_healthLock)
            {
                _lastStatus = status;
                
                // 更新连续失败计数
                if (status == FrameProcessStatus.Success || status == FrameProcessStatus.Recovered)
                {
                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                }
                
                // 更新总数和增量
                switch (status)
                {
                    case FrameProcessStatus.Recovered:
                        _totalRecoveredFrames++;
                        _deltaRecoveredFrames++;
                        break;
                    case FrameProcessStatus.Frozen:
                        _totalFrozenFrames++;
                        _deltaFrozenFrames++;
                        break;
                    case FrameProcessStatus.Dropped:
                        _totalDroppedFrames++;
                        _deltaDroppedFrames++;
                        break;
                }
                
                // 记录到最近窗口
                _recentFrames.Enqueue((timestamp, status));
                
                // ✅ 关键修复：更积极地清理过期记录，避免内存积累
                var cutoff = timestamp.AddSeconds(-RECENT_WINDOW_SECONDS);
                int cleaned = 0;
                while (_recentFrames.Count > 0 && _recentFrames.Peek().timestamp < cutoff)
                {
                    _recentFrames.Dequeue();
                    cleaned++;
                }
                
                // ✅ 如果队列仍然很大（超过窗口大小的2倍），强制清理更多
                const int MAX_RECENT_FRAMES = RECENT_WINDOW_SECONDS * 120; // 假设最大120fps
                while (_recentFrames.Count > MAX_RECENT_FRAMES)
                {
                    _recentFrames.Dequeue();
                    cleaned++;
                }
                
                if (cleaned > 0 && _logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Cleaned {Cleaned} expired frame records, current queue size: {Count}", cleaned, _recentFrames.Count);
                }
                
                // ✅ 计算帧间隔（优化：使用循环缓冲区避免频繁移除）
                if (_lastFrameTimestamp != DateTime.MinValue)
                {
                    var interval = (timestamp - _lastFrameTimestamp).TotalMilliseconds;
                    if (interval > 0 && interval < 1000) // 过滤异常值
                    {
                        _frameIntervals.Add(interval);
                        // ✅ 关键修复：只保留最近50个间隔（减少内存占用）
                        const int MAX_FRAME_INTERVALS = 50;
                        if (_frameIntervals.Count > MAX_FRAME_INTERVALS)
                        {
                            _frameIntervals.RemoveAt(0);
                        }
                    }
                }
                _lastFrameTimestamp = timestamp;
            }
        }

        public StreamHealthSnapshot GetHealthSnapshot(bool resetDeltas = false, bool resetStreamStats = false)
        {
            lock (_healthLock)
            {
                var now = DateTime.UtcNow;
                
                // 获取流统计信息
                ulong totalFrames = 0;
                ulong totalBytes = 0;
                double measuredBitrateMbps = 0;
                int framesLost = 0;
                int frameIndexPrev = -1;
                
                if (_videoReceiver != null)
                {
                    // 获取流统计信息
                    // 注意：GetAndResetStreamStats 会重置统计，所以每次调用都会获取自上次调用以来的增量
                    var (frames, bytes) = _videoReceiver.GetAndResetStreamStats();
                    totalFrames = frames;
                    totalBytes = bytes;
                    
                    // 计算码率（假设 60fps）
                    if (totalFrames > 0 && totalBytes > 0)
                    {
                        // 使用公式计算：bitrate = (bytes * 8 * fps) / frames
                        var bps = (totalBytes * 8UL * 60UL) / totalFrames;
                        measuredBitrateMbps = bps / 1000000.0;
                    }
                    
                    // 获取帧索引统计（也会重置）
                    var (prev, lost) = _videoReceiver.ConsumeAndResetFrameIndexStats();
                    frameIndexPrev = prev;
                    framesLost = lost;
                }
                
                // 计算最近窗口统计
                var cutoff = now.AddSeconds(-RECENT_WINDOW_SECONDS);
                int recentSuccess = 0;
                int recentRecovered = 0;
                int recentFrozen = 0;
                int recentDropped = 0;
                
                foreach (var (ts, status) in _recentFrames)
                {
                    if (ts >= cutoff)
                    {
                        switch (status)
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
                }
                
                // 计算 FPS
                double recentFps = 0;
                if (_recentFrames.Count > 0)
                {
                    var oldest = _recentFrames.Peek().timestamp;
                    var windowSeconds = (now - oldest).TotalSeconds;
                    if (windowSeconds > 0)
                    {
                        recentFps = _recentFrames.Count / windowSeconds;
                    }
                }
                
                // 计算平均帧间隔
                double avgInterval = 0;
                if (_frameIntervals.Count > 0)
                {
                    avgInterval = _frameIntervals.Average();
                }
                
                // 获取增量值（如果需要重置，先保存再重置）
                int deltaRecovered = _deltaRecoveredFrames;
                int deltaFrozen = _deltaFrozenFrames;
                int deltaDropped = _deltaDroppedFrames;
                
                if (resetDeltas)
                {
                    _deltaRecoveredFrames = 0;
                    _deltaFrozenFrames = 0;
                    _deltaDroppedFrames = 0;
                }
                
                // 注意：GetAndResetStreamStats 和 ConsumeAndResetFrameIndexStats 已经在上面的代码中调用了
                // 所以 resetStreamStats 参数实际上已经生效了
                
                return new StreamHealthSnapshot
                {
                    Timestamp = now,
                    LastStatus = _lastStatus,
                    Message = _consecutiveFailures > 0 ? $"连续失败 {_consecutiveFailures} 次" : null,
                    ConsecutiveFailures = _consecutiveFailures,
                    TotalRecoveredFrames = _totalRecoveredFrames,
                    TotalFrozenFrames = _totalFrozenFrames,
                    TotalDroppedFrames = _totalDroppedFrames,
                    DeltaRecoveredFrames = deltaRecovered,
                    DeltaFrozenFrames = deltaFrozen,
                    DeltaDroppedFrames = deltaDropped,
                    RecentWindowSeconds = RECENT_WINDOW_SECONDS,
                    RecentSuccessFrames = recentSuccess,
                    RecentRecoveredFrames = recentRecovered,
                    RecentFrozenFrames = recentFrozen,
                    RecentDroppedFrames = recentDropped,
                    RecentFps = recentFps,
                    AverageFrameIntervalMs = avgInterval,
                    LastFrameTimestampUtc = _lastFrameTimestamp,
                    TotalFrames = totalFrames,
                    TotalBytes = totalBytes,
                    MeasuredBitrateMbps = measuredBitrateMbps,
                    FramesLost = framesLost,
                    FrameIndexPrev = frameIndexPrev
                };
            }
        }

        #endregion

        #region IDR Detection
        
        /// <summary>
        /// ✅ 检测是否为IDR关键帧
        /// </summary>
        private bool IsIdrFrame(byte[] frameData)
        {
            if (frameData == null || frameData.Length < 10)
                return false;
            
            // 跳过header（如果有），查找NAL startcode
            int searchStart = 0;
            if (frameData.Length > 64)
            {
                // 可能有64字节的padding header
                searchStart = 64;
            }
            
            for (int i = searchStart; i < frameData.Length - 4; i++)
            {
                if (frameData[i] == 0x00 && frameData[i + 1] == 0x00)
                {
                    int nalStart = -1;
                    if (i + 3 < frameData.Length && frameData[i + 2] == 0x00 && frameData[i + 3] == 0x01)
                    {
                        nalStart = i + 4;
                    }
                    else if (i + 2 < frameData.Length && frameData[i + 2] == 0x01)
                    {
                        nalStart = i + 3;
                    }
                    
                    if (nalStart >= 0 && nalStart < frameData.Length)
                    {
                        byte nalHeader = frameData[nalStart];
                        
                        // H.264: NAL type 5 = IDR
                        byte h264Type = (byte)(nalHeader & 0x1F);
                        if (h264Type == 5)
                        {
                            return true;
                        }
                        
                        // H.265: NAL type 19/20 = IDR
                        byte hevcType = (byte)((nalHeader >> 1) & 0x3F);
                        if (hevcType == 19 || hevcType == 20)
                        {
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
        
        #endregion
    }
}

