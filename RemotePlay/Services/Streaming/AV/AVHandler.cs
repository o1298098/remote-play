using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Utils.Crypto;
using System.Collections.Concurrent;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// AV 处理器 V2 - 参考 chiaki-ng 的架构重新实现
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
            // 检测并处理 adaptive_stream_index 切换
            if (packet.Type == HeaderType.VIDEO && _adaptiveStreamManager != null)
            {
                var (switched, newProfile, needUpdateHeader) = _adaptiveStreamManager.CheckAndHandleSwitch(packet, _profileSwitchCallback);
                
                if (switched && needUpdateHeader && newProfile != null)
                {
                    // 更新 VideoReceiver 的 profiles
                    if (_videoReceiver != null && _videoProfiles != null)
                    {
                        _videoReceiver.SetStreamInfo(_videoProfiles);
                    }
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
                    if (_receiver != null && success)
                    {
                        var packetData = new byte[1 + frame.Length];
                        packetData[0] = (byte)HeaderType.VIDEO;
                        Array.Copy(frame, 0, packetData, 1, frame.Length);
                        try
                        {
                            _receiver.OnVideoPacket(packetData);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ OnVideoPacket 异常");
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
                            _logger.LogError(ex, "❌ OnAudioPacket 异常");
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
                try { data = _cipher.Decrypt(data, (int)packet.KeyPos); }
                catch (Exception ex) { _logger.LogError(ex, "❌ Decrypt failed frame={Frame}", packet.FrameIndex); }
            }
            return data;
        }

        #endregion

        #region Callbacks

        public void SetCorruptFrameCallbacks(Action<int, int>? videoCallback, Action<int, int>? audioCallback = null)
        {
            _videoCorruptCallback = videoCallback;
            _audioCorruptCallback = audioCallback;
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
                    _logger.LogWarning("⚠️ Video packet dropped in reorder queue: seq={Seq}, frame={Frame}",
                        droppedPacket.Index, droppedPacket.FrameIndex);
                },
                sizeStart: 64,
                sizeMin: 32,
                sizeMax: 256,
                timeoutMs: 200,
                dropStrategy: ReorderQueueDropStrategy.Begin,
                timeoutCallback: OnReorderQueueTimeout);
        }

        // 超时恢复机制：跟踪连续超时次数，超过阈值时请求关键帧
        private int _consecutiveTimeouts = 0;
        private DateTime _lastTimeoutTime = DateTime.MinValue;
        private const int MAX_CONSECUTIVE_TIMEOUTS = 10; // 连续超时10次后请求关键帧
        private const int TIMEOUT_WINDOW_MS = 2000; // 2秒内的超时才算连续

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
                _logger.LogWarning("⚠️ 连续超时 {Count} 次，请求关键帧恢复视频流", _consecutiveTimeouts);
                
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
                            _logger.LogInformation("✅ 已请求关键帧恢复视频流");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 请求关键帧失败");
                        }
                    });
                }
                else
                {
                    _logger.LogWarning("⚠️ 未设置 RequestKeyframeCallback，无法请求关键帧");
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

            _queue.Enqueue(packet);

            if (_queue.Count > 100 && (_workerTask == null || _workerTask.IsCompleted) && _cipher != null)
            {
                _logger.LogError("❌ Queue has {Size} packets but worker not running! Starting...", _queue.Count);
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

                while (!token.IsCancellationRequested && !_ct.IsCancellationRequested)
                {
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

                    if (_queue.IsEmpty)
                    {
                        Thread.Sleep(1);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }

                _queue.Clear();
                _logger.LogDebug("AVHandler2 worker stopped, total processed={Count}", processedCount);
            }, token);
        }

        #endregion

        #region Stop & Stats

        public void Stop()
        {
            _logger.LogDebug("🛑 AVHandler2.Stop() called");

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
                    var timeoutTask = Task.Delay(500);
                    var completedTask = Task.WhenAny(_workerTask, timeoutTask).GetAwaiter().GetResult();
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("⚠️ AVHandler2 worker 退出超时（500ms），强制继续");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ 等待 AVHandler2 worker 退出时发生异常");
                }
            }
        }

        public StreamPipelineStats GetAndResetStats()
        {
            // TODO: 实现统计信息
            return new StreamPipelineStats
            {
                VideoReceived = 0,
                VideoLost = 0,
                VideoTimeoutDropped = 0,
                AudioReceived = 0,
                AudioLost = 0,
                AudioTimeoutDropped = 0,
                PendingPackets = _queue.Count,
                FecAttempts = 0,
                FecSuccess = 0,
                FecFailures = 0,
                FecSuccessRate = 0.0
            };
        }

        public StreamHealthSnapshot GetHealthSnapshot(bool resetDeltas = false, bool resetStreamStats = false)
        {
            // TODO: 实现健康快照（简化版）
            return new StreamHealthSnapshot
            {
                Timestamp = DateTime.UtcNow,
                LastStatus = FrameProcessStatus.Success,
                Message = null,
                ConsecutiveFailures = 0,
                TotalRecoveredFrames = 0,
                TotalFrozenFrames = 0,
                TotalDroppedFrames = 0,
                DeltaRecoveredFrames = 0,
                DeltaFrozenFrames = 0,
                DeltaDroppedFrames = 0,
                RecentWindowSeconds = 10,
                RecentSuccessFrames = 0,
                RecentRecoveredFrames = 0,
                RecentFrozenFrames = 0,
                RecentDroppedFrames = 0,
                RecentFps = 0,
                AverageFrameIntervalMs = 0,
                LastFrameTimestampUtc = DateTime.UtcNow,
                TotalFrames = 0,
                TotalBytes = 0,
                MeasuredBitrateMbps = 0,
                FramesLost = 0,
                FrameIndexPrev = -1
            };
        }

        #endregion
    }
}

