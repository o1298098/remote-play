using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.Streaming;
using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Buffer;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Services.Streaming.Pipeline
{
    /// <summary>
    /// Video Pipeline - 负责视频包的重排序、拼帧和处理
    /// 设计目标：
    /// 1. 独立线程处理（不阻塞 Ingest）
    /// 2. ReorderQueue 管理乱序包
    /// 3. VideoReceiver 拼帧
    /// 4. 输出完整帧到 OutputPipeline
    /// </summary>
    public sealed class VideoPipeline : IDisposable
    {
        private readonly ILogger<VideoPipeline> _logger;
        private readonly ChannelReader<AVPacket> _inputReader;
        private readonly Channel<ProcessedFrame> _outputChannel;
        private readonly ReorderQueue<AVPacket>? _reorderQueue;
        private readonly VideoReceiver? _videoReceiver;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _workerTask;
        private readonly Task _reorderFlushTask;

        // 配置
        private readonly bool _enableReorder;
        private string? _detectedCodec;
        private VideoProfile[]? _videoProfiles;
        private AdaptiveStreamManager? _adaptiveStreamManager;
        private Action<VideoProfile, VideoProfile?>? _profileSwitchCallback;
        private Func<Task>? _requestKeyframeCallback;
        private StreamCipher? _cipher;  // ⚠️ 解密密钥（与旧的 AVHandler 一致）

        // 统计
        private long _totalReceived;
        private long _totalProcessed;
        private long _totalDropped;
        private long _framesComplete;
        private long _framesCorrupt;

        public VideoPipeline(
            ILogger<VideoPipeline> logger,
            ChannelReader<AVPacket> inputReader,
            ILoggerFactory loggerFactory,
            int outputCapacity = 512,
            bool enableReorder = true,
            int reorderWindowSize = 192,
            int reorderTimeoutMs = 1000)
        {
            _logger = logger;
            _inputReader = inputReader;
            _enableReorder = enableReorder;

            _outputChannel = Channel.CreateBounded<ProcessedFrame>(new BoundedChannelOptions(outputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

            // 初始化 VideoReceiver
            _videoReceiver = new VideoReceiver(loggerFactory.CreateLogger<VideoReceiver>());

            // 初始化 ReorderQueue
            if (_enableReorder)
            {
                _reorderQueue = new ReorderQueue<AVPacket>(
                    logger,
                    pkt => (uint)pkt.Index,
                    HandleOrderedPacket,
                    dropCallback: OnPacketDropped,
                    sizeStart: reorderWindowSize,
                    sizeMin: 128,
                    sizeMax: 512,
                    timeoutMs: reorderTimeoutMs,
                    dropStrategy: ReorderQueueDropStrategy.End,
                    maxOutputPerPull: 10,
                    timeoutCallback: OnReorderTimeout
                );

                // 启动定期 Flush 任务
                _reorderFlushTask = Task.Run(ReorderFlushLoop, _cts.Token);
            }
            else
            {
                _reorderFlushTask = Task.CompletedTask;
            }

            _workerTask = Task.Run(WorkerLoop, _cts.Token);
        }

        #region Public API

        /// <summary>
        /// 获取输出 Channel
        /// </summary>
        public ChannelReader<ProcessedFrame> OutputReader => _outputChannel.Reader;

        /// <summary>
        /// 设置视频配置
        /// </summary>
        public void SetStreamInfo(VideoProfile[]? videoProfiles)
        {
            _videoProfiles = videoProfiles;
            _videoReceiver?.SetStreamInfo(videoProfiles);
        }

        /// <summary>
        /// 设置视频编解码器
        /// </summary>
        public void SetVideoCodec(string codec)
        {
            _detectedCodec = codec;
        }

        /// <summary>
        /// 设置自适应流管理器
        /// </summary>
        public void SetAdaptiveStreamManager(AdaptiveStreamManager? manager, Action<VideoProfile, VideoProfile?>? onProfileSwitch = null)
        {
            _adaptiveStreamManager = manager;
            _profileSwitchCallback = onProfileSwitch;
        }

        /// <summary>
        /// 设置请求关键帧回调
        /// </summary>
        public void SetRequestKeyframeCallback(Func<Task>? callback)
        {
            _requestKeyframeCallback = callback;
        }

        /// <summary>
        /// 设置解密密钥（与旧的 AVHandler 一致）
        /// </summary>
        public void SetCipher(StreamCipher? cipher)
        {
            _cipher = cipher;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public VideoStats GetStats()
        {
            var reorderStats = _reorderQueue?.GetStats() ?? (0, 0, 0, 0);
            return new VideoStats
            {
                TotalReceived = Interlocked.Read(ref _totalReceived),
                TotalProcessed = Interlocked.Read(ref _totalProcessed),
                TotalDropped = Interlocked.Read(ref _totalDropped),
                FramesComplete = Interlocked.Read(ref _framesComplete),
                FramesCorrupt = Interlocked.Read(ref _framesCorrupt),
                ReorderProcessed = reorderStats.processed,
                ReorderDropped = reorderStats.dropped,
                ReorderTimeoutDropped = reorderStats.timeoutDropped,
                ReorderBufferSize = reorderStats.bufferSize,
                OutputQueueSize = _outputChannel.Reader.Count
            };
        }

        #endregion

        #region Worker Loop

        private async Task WorkerLoop()
        {
            _logger.LogInformation("✅ VideoPipeline worker started");

            try
            {
                await foreach (var packet in _inputReader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        Interlocked.Increment(ref _totalReceived);

                        // 自动检测编解码器
                        if (_detectedCodec == null)
                        {
                            DetectVideoCodec(packet);
                        }

                        // 处理自适应流切换
                        if (_adaptiveStreamManager != null)
                        {
                            _adaptiveStreamManager.CheckAndHandleSwitch(packet, _profileSwitchCallback);
                        }

                        // 推送到 ReorderQueue 或直接处理
                        if (_enableReorder && _reorderQueue != null)
                        {
                            _reorderQueue.Push(packet);
                            
                            // ✅ 游戏串流优化：更积极的flush策略，优先保证低延迟和稳定性
                            var stats = _reorderQueue.GetStats();
                            if (stats.bufferSize > 150)
                            {
                                // ✅ 积压严重时，立即flush，保证低延迟
                                _reorderQueue.Flush(force: false);
                            }
                            else if (stats.bufferSize > 80)
                            {
                                // ✅ 中等积压时，也进行flush，避免延迟累积
                                _reorderQueue.Flush(false);
                            }
                            // ✅ 积压不严重时，依赖ReorderFlushLoop定期flush（10ms间隔），保证低延迟
                        }
                        else
                        {
                            HandleOrderedPacket(packet);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ VideoPipeline processing error, frame={Frame}", packet.FrameIndex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ VideoPipeline worker exception");
            }
            finally
            {
                _logger.LogInformation("✅ VideoPipeline worker exited");
            }
        }

        private async Task ReorderFlushLoop()
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    _reorderQueue?.Flush(false);
                    
                    // ✅ 游戏串流优化：更频繁的flush，优先保证低延迟和稳定性
                    var stats = _reorderQueue?.GetStats() ?? (0, 0, 0, 0);
                    int delayMs;
                    if (stats.bufferSize > 200)
                    {
                        delayMs = 8;   // ✅ 积压非常严重时，8ms flush一次，快速处理
                    }
                    else if (stats.bufferSize > 100)
                    {
                        delayMs = 10;  // ✅ 积压严重时，10ms flush一次，快速处理
                    }
                    else if (stats.bufferSize > 50)
                    {
                        delayMs = 12;  // ✅ 中等积压时，12ms flush一次
                    }
                    else
                    {
                        delayMs = 10;  // ✅ 正常情况，10ms flush一次（约100fps处理能力），保证低延迟和稳定性
                    }
                    await Task.Delay(delayMs, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }

        #endregion

        #region Packet Processing

        private void HandleOrderedPacket(AVPacket packet)
        {
            try
            {
                if (_videoReceiver == null)
                {
                    _logger.LogWarning("⚠️ VideoReceiver is null");
                    return;
                }

                if (packet.Data.Length == 0)
                {
                    _logger.LogWarning("⚠️ Video packet has empty data, frame={Frame}, seq={Seq}", 
                        packet.FrameIndex, packet.Index);
                    return;
                }

                // 解密已在 IngestPipeline 中完成，packet.Data 已经是解密后的数据
                _videoReceiver.ProcessPacket(packet, packet.Data, (frame, recovered, success) =>
                {
                    Interlocked.Increment(ref _totalProcessed);

                    // 在宽限期内，即使success=false，如果recovered=true，也应该发送帧
                    // 避免在帧丢失后，因为参考帧缺失导致完全没有画面输出
                    if (success || recovered)
                    {
                        if (success)
                        {
                            Interlocked.Increment(ref _framesComplete);
                        }
                        else
                        {
                            // recovered=true 但 success=false，记录为恢复的帧
                            Interlocked.Increment(ref _framesCorrupt);
                            _logger.LogDebug("⚠️ Video frame recovered (not fully complete), frame={Frame}", packet.FrameIndex);
                        }

                        // 创建处理后的帧
                        var processedFrame = new ProcessedFrame
                        {
                            Type = FrameType.Video,
                            FrameIndex = packet.FrameIndex,
                            Data = frame,
                            Recovered = recovered,
                            Timestamp = DateTime.UtcNow,
                            IsKeyFrame = IsIdrFrame(frame)
                        };

                        // 推送到输出队列（非阻塞）
                        if (!_outputChannel.Writer.TryWrite(processedFrame))
                        {
                            Interlocked.Increment(ref _totalDropped);
                            _logger.LogWarning("⚠️ VideoPipeline output queue full, dropping frame={Frame}",
                                packet.FrameIndex);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _framesCorrupt);
                        _logger.LogDebug("⚠️ Video frame corrupt (not recovered), frame={Frame}", packet.FrameIndex);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ HandleOrderedPacket error, frame={Frame}", packet.FrameIndex);
            }
        }

        private void OnPacketDropped(AVPacket packet)
        {
            Interlocked.Increment(ref _totalDropped);
            var stats = _reorderQueue?.GetStats() ?? (0, 0, 0, 0);
            _logger.LogWarning(
                "⚠️ Video packet dropped in reorder queue: sseq={Seq}, frame={Frame}, reorderStats=processed:{Proc}/dropped:{Drop}/timeout:{Timeout}/buffer:{Buf}",
                packet.Index, packet.FrameIndex, stats.processed, stats.dropped, stats.timeoutDropped, stats.bufferSize);
        }

        private void OnReorderTimeout()
        {
            var stats = _reorderQueue?.GetStats() ?? (0, 0, 0, 0);
            _logger.LogWarning("⚠️ VideoPipeline reorder timeout, bufferSize={BufferSize}", stats.bufferSize);

            // ✅ 优化：避免过于激进的超时处理，减少画面冻结
            // ✅ 同时避免强制flush导致突然释放大量帧超过60fps
            if (_reorderQueue != null)
            {
                if (stats.bufferSize > 200)
                {
                    _logger.LogWarning("⚠️ ReorderQueue 积压严重（{Size}），普通 flush 以恢复画面（不强制，避免超过60fps）", stats.bufferSize);
                    // ✅ 使用普通flush而不是强制flush，让PullLockedLimited控制输出速率
                    _reorderQueue.Flush(force: false);
                    
                    // ✅ 只有在积压非常严重时才请求关键帧，避免频繁请求导致画面冻结
                    if (_requestKeyframeCallback != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _requestKeyframeCallback();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ Request keyframe failed");
                            }
                        });
                    }
                }
                else
                {
                    // ✅ 积压不严重时，只进行普通flush，不请求关键帧，避免打断正常流
                    // 普通flush会处理超时的包，但不会跳过所有等待的包
                    _reorderQueue.Flush(force: false);
                }
            }
        }

        #endregion

        #region Decryption

        /// <summary>
        /// 解密包数据（与旧的 AVHandler 完全一致）
        /// </summary>
        private byte[] DecryptPacket(AVPacket packet)
        {
            var data = packet.Data;
            if (_cipher != null && data.Length > 0 && packet.KeyPos > 0)
            {
                try 
                { 
                    data = _cipher.Decrypt(data, packet.KeyPos); 
                    }
                    catch (Exception ex) 
                    { 
                        _logger.LogError(ex, "❌ Decrypt failed frame={Frame}, keyPos={KeyPos}", packet.FrameIndex, packet.KeyPos); 
                    }
            }
            return data;
        }

        #endregion

        #region Codec Detection

        private void DetectVideoCodec(AVPacket packet)
        {
            string? codec = null;

            // 从 profile header 检测
            if (_videoProfiles != null && _videoProfiles.Length > 0)
            {
                codec = DetectCodecFromHeader(_videoProfiles[0].Header);
            }

            // 从包 Codec 字段检测
            if (codec == null)
            {
                codec = packet.Codec switch
                {
                    0x06 => "h264",
                    0x36 or 0x37 => "hevc",
                    _ => "h264"
                };
            }

            _detectedCodec = codec;
            _logger.LogInformation("📹 Detected video codec: {Codec}", codec);
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

        private bool IsIdrFrame(byte[] frameData)
        {
            if (frameData == null || frameData.Length < 10)
                return false;

            int searchStart = frameData.Length > 64 ? 64 : 0;

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
                            return true;

                        // H.265: NAL type 19/20 = IDR
                        byte hevcType = (byte)((nalHeader >> 1) & 0x3F);
                        if (hevcType == 19 || hevcType == 20)
                            return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                Task.WaitAll(new[] { _workerTask, _reorderFlushTask }, TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ VideoPipeline dispose error");
            }

            _outputChannel.Writer.Complete();
            _cts.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// 处理后的帧
    /// </summary>
    public struct ProcessedFrame
    {
        public FrameType Type { get; set; }
        public long FrameIndex { get; set; }
        public byte[] Data { get; set; }
        public bool Recovered { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsKeyFrame { get; set; }
    }

    /// <summary>
    /// 帧类型
    /// </summary>
    public enum FrameType
    {
        Video,
        Audio
    }

    /// <summary>
    /// Video Pipeline 统计信息
    /// </summary>
    public struct VideoStats
    {
        public long TotalReceived { get; set; }
        public long TotalProcessed { get; set; }
        public long TotalDropped { get; set; }
        public long FramesComplete { get; set; }
        public long FramesCorrupt { get; set; }
        public ulong ReorderProcessed { get; set; }
        public ulong ReorderDropped { get; set; }
        public ulong ReorderTimeoutDropped { get; set; }
        public int ReorderBufferSize { get; set; }
        public int OutputQueueSize { get; set; }
    }
}

