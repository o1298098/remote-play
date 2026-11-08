using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Services.Streaming.Pipeline
{
    /// <summary>
    /// AV Pipeline Coordinator - 协调所有 Pipeline 组件
    /// 
    /// 架构：
    /// Network → IngestPipeline (解析+解密)
    ///             ↓
    ///        PacketRouter (分发)
    ///         ↙        ↘
    ///   VideoPipeline  AudioPipeline (拼帧)
    ///         ↓            ↓
    ///        OutputPipeline (异步发送到 WebRTC)
    /// 
    /// 优势：
    /// 1. 完全异步，无阻塞
    /// 2. 各组件独立，易于调试
    /// 3. 背压保护
    /// 4. 性能监控
    /// </summary>
    public sealed class AVPipelineCoordinator : IDisposable
    {
        private readonly ILogger<AVPipelineCoordinator> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly string _hostType;
        private readonly CancellationToken _ct;

        // Pipeline 组件
        private readonly IngestPipeline _ingestPipeline;
        private readonly VideoPipeline _videoPipeline;
        private readonly AudioPipeline _audioPipeline;
        private readonly OutputPipeline _outputPipeline;

        // 路由任务
        private readonly CancellationTokenSource _routerCts = new();
        private readonly Task _routerTask;
        private readonly Task _packetRouterTask;

        // 配置
        private IAVReceiver? _receiver;

        public AVPipelineCoordinator(
            ILogger<AVPipelineCoordinator> logger,
            ILoggerFactory loggerFactory,
            string hostType,
            IAVReceiver receiver,
            CancellationToken ct)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _hostType = hostType;
            _receiver = receiver;
            _ct = ct;

            _videoInputChannel = CreateInputChannel(_ => Interlocked.Increment(ref _videoInputDropped));
            _audioInputChannel = CreateInputChannel(_ => Interlocked.Increment(ref _audioInputDropped));

            // 创建 Pipeline 组件
            _ingestPipeline = new IngestPipeline(
                loggerFactory.CreateLogger<IngestPipeline>(),
                hostType,
                inputCapacity: 2048,
                outputCapacity: 2048);

            var ingestOutput = _ingestPipeline.OutputReader;

            _videoPipeline = new VideoPipeline(
                loggerFactory.CreateLogger<VideoPipeline>(),
                CreateVideoPipelineInput(ingestOutput),
                loggerFactory,
                outputCapacity: 512,
                enableReorder: true,        
                reorderWindowSize: 256,
                // 队首缺包时整条流都会被卡住，超时过长会把每次丢包放大成可见卡顿
                reorderTimeoutMs: 30);

            _audioPipeline = new AudioPipeline(
                loggerFactory.CreateLogger<AudioPipeline>(),
                CreateAudioPipelineInput(ingestOutput),
                loggerFactory,
                outputCapacity: 512);

            _outputPipeline = new OutputPipeline(
                loggerFactory.CreateLogger<OutputPipeline>(),
                receiver,
                videoQueueCapacity: 256,
                audioQueueCapacity: 512);

            // 启动统一的包路由任务
            _packetRouterTask = Task.Run(RoutePacketsFromIngest, _routerCts.Token);

            // 启动帧路由任务
            _routerTask = Task.Run(async () =>
            {
                await Task.WhenAll(
                    RouteVideoFrames(),
                    RouteAudioFrames()
                );
            }, _routerCts.Token);

            _logger.LogInformation("✅ AVPipelineCoordinator initialized");
        }

    #region Pipeline Input Channels

    // 使用独立的 Channel，避免多读取者竞争；满时丢最旧的包并计数（处理速度跟不上的信号）
    private readonly Channel<AVPacket> _videoInputChannel;
    private readonly Channel<AVPacket> _audioInputChannel;

    // 上游诊断：网络缺包（序号跳号）与内部队列丢弃
    private long _ingestDropped;
    private long _videoInputDropped;
    private long _audioInputDropped;
    private long _videoNetworkGaps;
    private long _audioNetworkGaps;
    private long _videoRoutedWindow;
    private DateTime _lastUpstreamLog = DateTime.UtcNow;
    private static readonly TimeSpan UpstreamLogInterval = TimeSpan.FromSeconds(5);

    private static Channel<AVPacket> CreateInputChannel(Action<AVPacket> onDropped) =>
        Channel.CreateBounded<AVPacket>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        }, onDropped);

    private ChannelReader<AVPacket> CreateVideoPipelineInput(ChannelReader<AVPacket> source)
    {
        return _videoInputChannel.Reader;
    }

    private ChannelReader<AVPacket> CreateAudioPipelineInput(ChannelReader<AVPacket> source)
    {
        return _audioInputChannel.Reader;
    }

    #endregion

    #region Routing Tasks

    /// <summary>
    /// 统一的包路由任务：从 Ingest 读取包并根据类型分发到 Video/Audio Pipeline
    /// 统一的包路由任务：从 Ingest 读取包并根据类型分发到 Video/Audio Pipeline
    /// </summary>
    private async Task RoutePacketsFromIngest()
    {
        long totalRouted = 0;
        long videoRouted = 0;
        long audioRouted = 0;
        long unknownType = 0;
        int lastVideoIndex = -1;
        int lastAudioIndex = -1;

        try
        {
            await foreach (var packet in _ingestPipeline.OutputReader.ReadAllAsync(_routerCts.Token))
            {
                totalRouted++;

                // 根据类型分发到对应的 Pipeline
                if (packet.Type == HeaderType.VIDEO)
                {
                    videoRouted++;
                    _videoRoutedWindow++;
                    _videoNetworkGaps += CountSequenceGap(ref lastVideoIndex, packet.Index);
                    await _videoInputChannel.Writer.WriteAsync(packet, _routerCts.Token);
                }
                else if (packet.Type == HeaderType.AUDIO)
                {
                    audioRouted++;
                    _audioNetworkGaps += CountSequenceGap(ref lastAudioIndex, packet.Index);
                    await _audioInputChannel.Writer.WriteAsync(packet, _routerCts.Token);
                }
                else
                {
                    unknownType++;
                    _logger.LogWarning("⚠️ Unknown packet type: {Type}", packet.Type);
                }

                LogUpstreamStatsIfDue();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ RoutePacketsFromIngest exception");
        }
        finally
        {
            _videoInputChannel.Writer.Complete();
            _audioInputChannel.Writer.Complete();
            _logger.LogInformation(
                "🛑 PacketRouter stopped. Total={Total}, Video={Video}, Audio={Audio}, Unknown={Unknown}",
                totalRouted, videoRouted, audioRouted, unknownType
            );
        }
    }

        /// <summary>
        /// 按 16 位包序号统计跳号数量（进入服务端之前就已缺失的包，乱序到达的包会被计入一次后不再扣回）
        /// </summary>
        private static long CountSequenceGap(ref int lastIndex, ushort index)
        {
            int previous = lastIndex;
            lastIndex = index;
            if (previous < 0)
            {
                return 0;
            }

            int delta = (index - previous) & 0xFFFF;
            return delta is > 1 and < 0x8000 ? delta - 1 : 0;
        }

        private void LogUpstreamStatsIfDue()
        {
            var now = DateTime.UtcNow;
            if (now - _lastUpstreamLog < UpstreamLogInterval)
            {
                return;
            }
            _lastUpstreamLog = now;

            long received = _videoRoutedWindow;
            long gaps = _videoNetworkGaps;
            long audioGaps = _audioNetworkGaps;
            long ingestDropped = Interlocked.Exchange(ref _ingestDropped, 0);
            long videoDropped = Interlocked.Exchange(ref _videoInputDropped, 0);
            long audioDropped = Interlocked.Exchange(ref _audioInputDropped, 0);
            _videoRoutedWindow = 0;
            _videoNetworkGaps = 0;
            _audioNetworkGaps = 0;

            bool internalDrops = ingestDropped > 0 || videoDropped > 0 || audioDropped > 0;
            if (!internalDrops && gaps == 0 && audioGaps == 0)
            {
                return;
            }

            double lossRate = received + gaps > 0 ? (double)gaps / (received + gaps) : 0;
            // 只有服务端内部丢包才需要关注；单纯的网络缺包由主机的码率自适应处理
            var level = internalDrops ? LogLevel.Warning : LogLevel.Debug;
            _logger.Log(level,
                "Upstream packet stats (5s): received={Received} network gaps={Gaps} ({Loss:P1}) audio gaps={AudioGaps} | server-side drops: decrypt queue={Ingest} video queue={Video} audio queue={Audio}",
                received, gaps, lossRate, audioGaps, ingestDropped, videoDropped, audioDropped);
        }

        private async Task RouteVideoFrames()
        {
            try
            {
                long frameCount = 0;
                await foreach (var frame in _videoPipeline.OutputReader.ReadAllAsync(_routerCts.Token))
                {
                    frameCount++;
                    bool pushed = _outputPipeline.TryPushVideoFrame(frame);
                    if (!pushed)
                    {
                        _logger.LogWarning("⚠️ RouteVideoFrames: Failed to push frame={Frame} to OutputPipeline", frame.FrameIndex);
                    }
                    else if (frameCount % 100 == 0)
                    {
                        _logger.LogDebug("🔍 RouteVideoFrames: Routed {Count} frames to OutputPipeline", frameCount);
                    }
                }
                _logger.LogDebug("🔍 RouteVideoFrames: Total routed {Count} frames", frameCount);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ RouteVideoFrames exception");
            }
        }

        private async Task RouteAudioFrames()
        {
            try
            {
                await foreach (var frame in _audioPipeline.OutputReader.ReadAllAsync(_routerCts.Token))
                {
                    _outputPipeline.TryPushAudioFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ RouteAudioFrames exception");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// 添加网络包（入口点）
        /// </summary>
        public void AddPacket(byte[] msg)
        {
            if (!_ingestPipeline.TryPushRawData(msg))
            {
                Interlocked.Increment(ref _ingestDropped);
            }
        }

        /// <summary>
        /// 设置接收器
        /// 设置接收器，同时更新 OutputPipeline 的 receiver
        /// </summary>
        public void SetReceiver(IAVReceiver receiver)
        {
            _receiver = receiver;
            _outputPipeline.SetReceiver(receiver);
            _logger.LogInformation("✅ AVPipelineCoordinator: Receiver switched to {Receiver}", receiver?.GetType().Name ?? "null");
        }

        /// <summary>
        /// 设置解密密钥
        /// 设置解密密钥
        /// 解密在 IngestPipeline 中串行进行，避免并行解密导致 keyPos 混乱
        /// </summary>
        public void SetCipher(StreamCipher? cipher)
        {
            _ingestPipeline.SetCipher(cipher);
            // VideoPipeline 和 AudioPipeline 不再需要 cipher（解密已在 IngestPipeline 中完成）
        }

        /// <summary>
        /// 设置视频配置
        /// </summary>
        public void SetHeaders(byte[]? videoHeader, byte[]? audioHeader, VideoProfile[]? videoProfiles)
        {
            if (videoProfiles != null && videoProfiles.Length > 0)
            {
                _videoPipeline.SetStreamInfo(videoProfiles);
            }

            if (audioHeader != null)
            {
                _audioPipeline.SetHeader(audioHeader);
            }
        }

        /// <summary>
        /// 设置自适应流管理器
        /// </summary>
        public void SetAdaptiveStreamManager(AdaptiveStreamManager? manager, Action<VideoProfile, VideoProfile?>? onProfileSwitch = null)
        {
            _videoPipeline.SetAdaptiveStreamManager(manager, onProfileSwitch);
        }

        /// <summary>
        /// 设置请求关键帧回调
        /// </summary>
        public void SetRequestKeyframeCallback(Func<Task>? callback)
        {
            _videoPipeline.SetRequestKeyframeCallback(callback);
        }

        /// <summary>
        /// 设置帧丢失回调
        /// </summary>
        public void SetFrameLossCallback(Action<int>? callback)
        {
            _audioPipeline.SetFrameLossCallback(callback);
        }

        /// <summary>
        /// 获取完整统计信息
        /// </summary>
        public PipelineStats GetStats()
        {
            return new PipelineStats
            {
                Ingest = _ingestPipeline.GetStats(),
                Video = _videoPipeline.GetStats(),
                Audio = _audioPipeline.GetStats(),
                Output = _outputPipeline.GetStats()
            };
        }

        /// <summary>
        /// 停止所有 Pipeline
        /// </summary>
        public void Stop()
        {
            _logger.LogInformation("🛑 Stopping AVPipelineCoordinator...");
            _routerCts.Cancel();
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Stop();

            try
            {
                Task.WaitAll(new[] { _packetRouterTask, _routerTask }, TimeSpan.FromMilliseconds(500));
            }
            catch { }

            _ingestPipeline.Dispose();
            _videoPipeline.Dispose();
            _audioPipeline.Dispose();
            _outputPipeline.Dispose();
            _routerCts.Dispose();

            _logger.LogInformation("✅ AVPipelineCoordinator disposed");
        }

        #endregion
    }

    /// <summary>
    /// 完整的 Pipeline 统计信息
    /// </summary>
    public struct PipelineStats
    {
        public IngestStats Ingest { get; set; }
        public VideoStats Video { get; set; }
        public AudioStats Audio { get; set; }
        public OutputStats Output { get; set; }

        public override string ToString()
        {
            return $"Ingest: Received={Ingest.TotalReceived}, Parsed={Ingest.TotalParsed}, " +
                   $"Video: Received={Video.TotalReceived}, Complete={Video.FramesComplete}, Dropped={Video.TotalDropped}, " +
                   $"Audio: Received={Audio.TotalReceived}, Complete={Audio.FramesComplete}, " +
                   $"Output: VideoSent={Output.VideoFramesSent}, AudioSent={Output.AudioFramesSent}";
        }
    }
}

