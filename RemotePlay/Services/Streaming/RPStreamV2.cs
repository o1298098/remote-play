using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Quality;
using RemotePlay.Utils.Crypto;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RemotePlay.Services.Streaming
{
    /// <summary>
    /// RPStream - 协议流处理类
    /// 设计原则：
    /// 1. 单一职责：每个方法只做一件事
    /// 2. 清晰的状态管理：STATE_INIT -> STATE_READY
    /// 3. 依赖注入：使用 ILogger、ILoggerFactory
    /// 4. 易于维护：代码结构清晰，注释完整
    /// </summary>
    public sealed class RPStreamV2 : IDisposable
    {
        #region Constants 

        private const int STREAM_PORT = 9296;
        private const int TEST_STREAM_PORT = 9297;
        private const int A_RWND = 0x019000;
        private const byte OUTBOUND_STREAMS = 0x64;
        private const byte INBOUND_STREAMS = 0x64;
        private const int DEFAULT_RTT = 1;
        private const int DEFAULT_MTU = 1454;
        private const int UDP_RECEIVE_BUFFER_SIZE = 1 << 20; // 1MB
        private const int MAX_PROCESSED_TSNS = 1000;

        // 状态常量
        private const string STATE_INIT = "init";
        private const string STATE_READY = "ready";

        // BIG 重试常量
        private const int BIG_RETRY_DELAY_MS = 1000;
        private const int BIG_MAX_RETRIES = 5;
        
        // 心跳常量
        private const int HEARTBEAT_INTERVAL_MS = 1000; // 心跳间隔 1 秒
        private const int HEARTBEAT_LOG_INTERVAL = 10; // 每 10 次心跳记录一次日志
		private const double DUALSENSE_WEAK_MULTIPLIER = 0.33;
		private const double DUALSENSE_MEDIUM_MULTIPLIER = 0.5;

		private enum TakionDataType : byte
		{
			Protobuf = 0,
			Rumble = 7,
			PadInfo = 9,
			TriggerEffects = 11
		}

        #endregion

        #region Fields

        private readonly ILogger<RPStreamV2> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly RemoteSession _session;
        private readonly string _host;
        private readonly int _port;
        private readonly CancellationToken _cancellationToken;

        // 网络
        private UdpClient? _udpClient;
        private IPEndPoint? _remoteEndPoint;
        private Task? _receiveLoopTask;
        
        // ✅ 流断开检测
        private DateTime _lastPacketReceivedTime = DateTime.UtcNow;
        private const int STREAM_TIMEOUT_SECONDS = 30; // 30 秒没有收到任何包，认为流已断开

        // 状态
        private string? _state;
        private uint _tsn = 1;
        private uint _tagLocal = 1;
        private uint _tagRemote = 0;
        private bool _isReady = false;
        private bool _isStopping = false;
        private bool _isReconnecting = false; // ✅ 标记是否正在进行流重置/重连

        // 加密
        private StreamECDH? _ecdh;
        private StreamCipher? _cipher;

        // AV 处理
        private AV.AVHandler? _avHandler;

        // 接收器
        private IAVReceiver? _receiver;

        // 去重跟踪
        private readonly HashSet<uint> _processedTsns = new();
        private readonly Queue<uint> _processedTsnsQueue = new();
        private readonly object _sendLock = new();

        // 回调
        private Action? _ackCallback;
        private uint _ackCallbackTsn = 0;

        // StreamInfo 缓存
        private byte[]? _cachedVideoHeader;
        private byte[]? _cachedAudioHeader;

        // BIG 重试
        private byte[]? _lastBigPayload;

        // ✅ Feedback 和 Congestion 服务
        private FeedbackSenderService? _feedbackSender;
        private CongestionControlService? _congestionControl;
        
        // ✅ 自适应流管理器
        private AdaptiveStreamManager? _adaptiveStreamManager;
        
        // 心跳循环任务
        private Task? _heartbeatLoopTask;
        
        // 断开连接回调
        private Func<Task>? _onDisconnectCallback;

		// 手柄反馈状态
		private readonly object _rumbleLock = new();
		private double _rumbleMultiplier = 1.0;
		private int _ps5RumbleIntensity = 0x00;
		private int _ps5TriggerIntensity = 0x00;
		private byte _currentHapticIntensityCode = 0xFF;
		private byte _currentTriggerIntensityCode = 0xFF;
		private readonly byte[] _ledState = new byte[3];
		private byte _playerIndex;
        private StreamHealthSnapshot _healthSnapshot = default;
        private StreamPipelineStats _lastPipelineStats = default;
        private int _consecutiveSevereFailures = 0;
        private int _consecutiveSuccessFrames = 0; // ✅ 连续成功帧数（用于更严格的流健康恢复判断）
        private int _lastFallbackFrameIndex = -1; // ✅ 最后一次 fallback 的帧索引（用于判断是否真正恢复）
        private DateTime _lastFallbackTime = DateTime.MinValue; // ✅ 最后一次 fallback 的时间（用于判断是否真正恢复）
        private DateTime _lastDegradeAction = DateTime.MinValue;
        private DateTime _lastKeyframeRequest = DateTime.MinValue;
		private readonly TimeSpan _keyframeRequestCooldown = TimeSpan.FromSeconds(1.0); // 冷却时间 1 秒，避免过度请求
        private readonly TimeSpan _idrMetricsWindow = TimeSpan.FromSeconds(30);
        private readonly Queue<DateTime> _idrRequestHistory = new();
        private readonly object _idrMetricsLock = new();
        private int _totalIdrRequests = 0;
        
        // ✅ 流健康恢复阈值（需要连续成功多次才认为恢复）
        private const int RECOVERY_SUCCESS_THRESHOLD = 10; // ✅ 增加到 10 帧，更严格的恢复判断
        private const int RECOVERY_FRAME_INDEX_THRESHOLD = 3; // ✅ 需要在 fallback 后至少处理 3 个新帧才认为恢复
        private static readonly TimeSpan RECOVERY_MIN_DURATION = TimeSpan.FromSeconds(2); // ✅ 需要至少 2 秒的稳定时间才认为恢复
        // ✅ 当 cipher 未就绪时延迟发送 IDR 的挂起标记
        private bool _idrPending = false;

        #endregion

		#region Events

		public event EventHandler<RumbleEventArgs>? RumbleReceived;

		#endregion

        #region Constructor

        public RPStreamV2(
            ILogger<RPStreamV2> logger,
            ILoggerFactory loggerFactory,
            RemoteSession session,
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _cancellationToken = cancellationToken;

            // 初始化 AVHandler2
            _avHandler = new AV.AVHandler(
                _loggerFactory.CreateLogger<AV.AVHandler>(),
                _session.HostType,
                null, // cipher 稍后设置
                null, // receiver 稍后设置
                _cancellationToken
            );

            _avHandler.SetCorruptFrameCallbacks(
                (start, end) =>
                {
                    if (_cipher == null || _isStopping)
                        return;

                    if (end < start)
                    {
                        var tmp = start;
                        start = end;
                        end = tmp;
                    }
                    SendCorrupt(start, end);
                });
            _avHandler.SetStreamHealthCallback(OnStreamHealthEvent);
            
            // ✅ 初始化 FeedbackSender 服务
            _feedbackSender = new FeedbackSenderService(
                _loggerFactory.CreateLogger<FeedbackSenderService>(),
                SendFeedbackPacketAsync  // 发送回调
            );
            
            // ✅ 初始化 CongestionControl 服务
            _congestionControl = new CongestionControlService(
                _loggerFactory.CreateLogger<CongestionControlService>(),
                SendRawPacketAsync,  // 发送原始包回调
                GetCurrentKeyPos,     // 获取 key_pos 回调
                GetPacketStats        // 获取包统计回调（可选）
            );

            // ✅ 初始化 AdaptiveStreamManager
            _adaptiveStreamManager = new AdaptiveStreamManager(
                _loggerFactory.CreateLogger<AdaptiveStreamManager>());

            // 将 manager 传递给 AVHandler
            _avHandler.SetAdaptiveStreamManager(_adaptiveStreamManager, OnProfileSwitched);

            // ✅ 设置请求关键帧回调（用于超时恢复）
            _avHandler.SetRequestKeyframeCallback(RequestKeyframeAsync);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 启动流
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation("Starting RPStream to {Host}:{Port}", _host, _port);

            // 初始化 UDP 客户端
            InitializeUdpClient();

            // 设置远程端点
            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(_host), _port);

            // 启动接收循环
            _receiveLoopTask = Task.Run(ReceiveLoopAsync, _cancellationToken);

            // 设置状态并发送 INIT
            _state = STATE_INIT;
            SendInit();

            _logger.LogInformation("RPStream started, state={State}, tsn={Tsn}, tagLocal={TagLocal}",
                _state, _tsn, _tagLocal);
        }

        /// <summary>
        /// 停止流
        /// </summary>
        public async Task StopAsync()
        {
            // 防止重复停止
            if (_isStopping)
            {
                _logger.LogDebug("Already stopping, skipping");
                return;
            }
            
            _isStopping = true;
            _isReconnecting = false; // ✅ 清除重连标志
            _isReady = false; // ✅ 立即停止心跳循环和卡顿检测任务
            _logger.LogInformation("Stopping RPStream");

            try
            {
                
                // ✅ 先停止 Feedback 和 Congestion 服务
                if (_feedbackSender != null)
                {
                    await _feedbackSender.StopAsync();
                    _feedbackSender.Dispose();
                }
                
                if (_congestionControl != null)
                {
                    await _congestionControl.StopAsync();
                    _congestionControl.Dispose();
                }
                
                // 停止 AVHandler
                _avHandler?.Stop();

                // 发送 DISCONNECT
                if (_cipher != null)
                {
                    var disconnectData = ProtoHandler.DisconnectPayload();
                    SendData(disconnectData, channel: 1, flag: 1, proto: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during disconnect");
            }

            // ✅ 等待所有任务退出（最多等待 2 秒，避免阻塞太久）
            var tasksToWait = new List<Task>();
            if (_receiveLoopTask != null && !_receiveLoopTask.IsCompleted)
                tasksToWait.Add(_receiveLoopTask);
            if (_heartbeatLoopTask != null && !_heartbeatLoopTask.IsCompleted)
                tasksToWait.Add(_heartbeatLoopTask);
            
            if (tasksToWait.Count > 0)
            {
                try
                {
                    // ✅ 使用更短的超时时间（2秒），如果任务没有响应，直接继续
                    var timeoutTask = Task.Delay(2000);
                    var completedTask = await Task.WhenAny(Task.WhenAll(tasksToWait), timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("⚠️ 等待任务退出超时（2秒），强制继续停止流程");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ 等待任务退出时发生异常，继续停止流程");
                }
            }

            // 关闭 UDP（在接收循环退出后）
            try
            {
                _udpClient?.Dispose();
                _udpClient = null;
            }
            catch { }

            _logger.LogInformation("RPStream stopped");
        }

        /// <summary>
        /// 设置断开连接回调
        /// </summary>
        public void SetOnDisconnectCallback(Func<Task>? callback)
        {
            _onDisconnectCallback = callback;
        }

        /// <summary>
        /// 添加/切换接收器
        /// ✅ 支持实时切换：如果已有 receiver，会切换到新的，并自动同步当前状态
        /// </summary>
        public void AddReceiver(IAVReceiver receiver)
        {
            if (receiver == null)
                throw new ArgumentNullException(nameof(receiver));

            var oldReceiver = _receiver;
            _receiver = receiver;
            _avHandler?.SetReceiver(receiver);

            // 通知 receiver 进入等待 IDR 模式
            receiver.EnterWaitForIdr();
            
            // 重新启动 IDR requester，确保新 receiver 从关键帧开始
            //_ = Task.Run(async () =>
            //{
            //    await Task.Delay(100);
            //    await StartIdrRequesterAsync();
            //});

            if (oldReceiver != null)
            {
                _logger.LogInformation("Switching receiver from {OldType} to {NewType}, requesting new IDR frame", 
                    oldReceiver.GetType().Name, receiver.GetType().Name);
                
                // ✅ AVHandler.SetReceiver 会自动同步 StreamInfo 和 codec
                // 这里不需要额外处理，因为 AVHandler 已经处理了状态同步
                
                // ✅ 关键修复：重新发送 CONTROLLER_CONNECTION，确保控制器连接状态保持
                // 当切换receiver时，PlayStation可能认为控制器断开，需要重新发送连接消息
                if (_isReady && _cipher != null)
                {
                    SendControllerConnection();
                }
            }
            else
            {
                _logger.LogInformation("Receiver added to RPStream: {Type}", receiver.GetType().Name);
                
                // 如果已有 StreamInfo，AVHandler.SetReceiver 会自动发送
                // 但为了兼容性，这里也发送一次（如果 AVHandler 还没有 headers）
                // 实际上，AVHandler.SetReceiver 已经会检查并发送了
                if (_cachedVideoHeader != null || _cachedAudioHeader != null)
                {
                    // ✅ 对齐：视频 header 需要添加 FFMPEG_PADDING
                    byte[] videoHeader = _cachedVideoHeader ?? Array.Empty<byte>();
                    if (_cachedVideoHeader != null && _cachedVideoHeader.Length > 0)
                    {
                        var padding = new byte[64];
                        var paddedHeader = new byte[_cachedVideoHeader.Length + padding.Length];
                        System.Buffer.BlockCopy(_cachedVideoHeader, 0, paddedHeader, 0, _cachedVideoHeader.Length);
                        System.Buffer.BlockCopy(padding, 0, paddedHeader, _cachedVideoHeader.Length, padding.Length);
                        videoHeader = paddedHeader;
                    }
                    receiver.OnStreamInfo(
                        videoHeader,
                        _cachedAudioHeader ?? Array.Empty<byte>()
                    );
                }
            }
        }

        /// <summary>
        /// 发送损坏帧通知
        /// </summary>
        public void SendCorrupt(int start, int end)
        {
            var data = ProtoHandler.CorruptFrame(start, end);
			// CORRUPTFRAME 使用 flag=1, channel=2
			SendData(data, channel: 2, flag: 1, proto: true);
        }

        /// <summary>
        /// 发送反馈
        /// 注意：反馈包有自己的格式，不需要经过 SendPacket 的通用处理
        /// 反馈包格式：type(1) + sequence(2) + padding(1) + key_pos(4) + gmac(4) + payload
        /// 应该直接通过 UDP 发送，不做任何修改
        /// </summary>
        public void SendFeedback(int feedbackType, int sequence, byte[]? data = null, ControllerState? state = null)
        {
            // 如果正在停止，直接返回
            if (_isStopping)
            {
                return;
            }
            
            if (_cipher == null)
            {
                if (!_isStopping)
                {
                    _logger.LogWarning("Cannot send feedback: cipher not initialized");
                }
                return;
            }

            if (_udpClient == null || _remoteEndPoint == null)
            {
                if (!_isStopping)
                {
                    _logger.LogWarning("Cannot send feedback: UDP client or remote endpoint is null");
                }
                return;
            }

            byte[] feedbackPacket;
            if (feedbackType == (int)HeaderType.FEEDBACK_STATE)
            {
                // 如果有 state，需要构建 state data
                var stateData = state != null 
                    ? ProtoHandler.FeedbackState(_session.HostType, state) 
                    : (data ?? Array.Empty<byte>());
                feedbackPacket = FeedbackPacket.CreateFeedbackState((ushort)sequence, stateData, _cipher);
            }
            else
            {
                feedbackPacket = FeedbackPacket.CreateEvent((ushort)sequence, data ?? Array.Empty<byte>(), _cipher);
            }

            // ✅ 直接通过 UDP 发送反馈包，不经过 SendPacket 的通用处理
            // Python 中的 send() 只是简单地通过 UDP socket 发送，不做任何修改
            lock (_sendLock)
            {
                try
                {
                    _udpClient.Send(feedbackPacket, feedbackPacket.Length, _remoteEndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send feedback packet: type={Type}, sequence={Sequence}", 
                        feedbackType, sequence);
                }
            }
        }

        /// <summary>
        /// 更新控制器状态到 FeedbackSenderService
        /// 用于同步摇杆、按键等控制器输入
        /// </summary>
        public void UpdateControllerState(ControllerState state)
        {
            _feedbackSender?.UpdateControllerState(state);
        }

        /// <summary>
        /// 发送拥塞控制包
        /// </summary>
        public void SendCongestion(int received, int lost)
        {
            // 如果正在停止，直接返回
            if (_isStopping)
            {
                return;
            }
            
            if (_cipher == null)
            {
                if (!_isStopping)
                {
                    _logger.LogWarning("Cannot send congestion: cipher not initialized");
                }
                return;
            }

            var congestionData = ProtoHandler.Congestion(received, lost);
            var congestionPacket = FeedbackPacket.CreateCongestion(0, congestionData, _cipher);
            SendRaw(congestionPacket);
        }

        #endregion

        #region Initialization Methods

        /// <summary>
        /// 初始化 UDP 客户端
        /// </summary>
        private void InitializeUdpClient()
        {
            _udpClient = new UdpClient();
            _udpClient.Client.ReceiveBufferSize = UDP_RECEIVE_BUFFER_SIZE;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udpClient.DontFragment = true;
        }

        /// <summary>
        /// 发送 INIT 包
        /// </summary>
        private void SendInit()
        {
            var initPacket = Packet.CreateInit(_tagLocal, _tsn);
            SendRaw(initPacket);
            _logger.LogInformation("INIT sent: tagLocal={TagLocal}, tsn={Tsn}", _tagLocal, _tsn);
        }

        /// <summary>
        /// 发送 COOKIE 包
        /// </summary>
        private void SendCookie(byte[] cookieData)
        {
            var cookiePacket = Packet.CreateCookie(_tagLocal, _tagRemote, cookieData);
            SendRaw(cookiePacket);
            _logger.LogInformation("COOKIE sent: tagLocal={TagLocal}, tagRemote={TagRemote}, len={Len}",
                _tagLocal, _tagRemote, cookieData.Length);
        }

        /// <summary>
        /// 发送 BIG 负载
        /// </summary>
        private void SendBig()
        {
            int version = _session.HostType.Equals("PS5", StringComparison.OrdinalIgnoreCase) ? 12 : 9;

            // 创建 ECDH
            _ecdh = new StreamECDH();

            // 构建 LaunchSpec
            var launchSpecRaw = BuildLaunchSpec();
            var launchSpecEnc = ProtoHandler.EncodeLaunchSpecWithSession(
                _session.HostType,
                _session.Secret,
                _session.SessionIv,
                launchSpecRaw
            );

            // 构建 BIG 负载
            var bigPayload = ProtoCodec.BuildBigPayload(
                clientVersion: version,
                sessionKey: _session.SessionId ?? Array.Empty<byte>(),
                launchSpec: launchSpecEnc,
                encryptedKey: new byte[4],
                ecdhPub: _ecdh.PublicKey,
                ecdhSig: _ecdh.PublicSig
            );

            _logger.LogInformation("Sending BIG payload: len={Len}, tagRemote={TagRemote}", 
                bigPayload.Length, _tagRemote);
            
            // 保存 BIG payload 用于重试
            _lastBigPayload = bigPayload;
            
            // 发送 BIG（此时没有 cipher，所以不需要加密）
            // 但我们需要确保 tag_remote 已设置
            if (_tagRemote == 0)
            {
                _logger.LogError("Cannot send BIG: tagRemote is 0");
                return;
            }
            
            SendData(bigPayload, channel: 1, flag: 1);
            
            // 启动重试循环
            StartBigRetryLoop();
        }

        /// <summary>
        /// 启动 BIG 重试循环
        /// </summary>
        private void StartBigRetryLoop()
        {
            _ = Task.Run(async () =>
            {
                int retries = 0;
                while (!_cancellationToken.IsCancellationRequested && 
                       !_isReady && 
                       _cipher == null && 
                       retries < BIG_MAX_RETRIES)
                {
                    try 
                    { 
                        await Task.Delay(BIG_RETRY_DELAY_MS, _cancellationToken); 
                    } 
                    catch 
                    { 
                        break; 
                    }
                    
                    if (_isReady || _cipher != null) 
                        break;
                    
                    retries++;
                    _logger.LogWarning("BIG retry #{Retry}/{Max}, waiting for BANG response", 
                        retries, BIG_MAX_RETRIES);
                    
                    if (_lastBigPayload != null)
                    {
                        SendData(_lastBigPayload, channel: 1, flag: 1);
                    }
                }
                
                if (_cipher == null && !_cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError("BIG failed after {Retries} retries, no BANG response received", retries);
                }
            }, _cancellationToken);
        }

        /// <summary>
        /// 构建 LaunchSpec
        /// </summary>
        private byte[] BuildLaunchSpec()
        {
            int rtt = _session.RttUs > 0 ? (int)(_session.RttUs / 1000) : DEFAULT_RTT;
            int mtu = _session.MtuOut > 0 ? _session.MtuOut : DEFAULT_MTU;
            var launchOptions = _session.LaunchOptions ?? StreamLaunchOptionsResolver.Resolve(_session);

            return ProtoHandler.BuildLaunchSpec(
                _session.SessionId,
                _session.HostType,
                _ecdh!.HandshakeKey,
                width: launchOptions.Width,
                height: launchOptions.Height,
                fps: launchOptions.Fps,
                bitrateKbps: launchOptions.BitrateKbps,
                videoCodec: launchOptions.VideoCodec,
                hdr: launchOptions.Hdr,
                rtt: rtt,
                mtu: mtu
            );
        }

        #endregion

        #region Receive Loop

        /// <summary>
        /// 接收循环
        /// </summary>
        private async Task ReceiveLoopAsync()
        {
            if (_udpClient == null) return;

            while (!_cancellationToken.IsCancellationRequested && !_isStopping)
            {
                try
                {
                    // 检查 UDP 客户端是否已释放
                    if (_udpClient == null || _isStopping)
                    {
                        break;
                    }
                    
                    // ✅ 关键修复：添加超时机制防止 ReceiveAsync 无限阻塞
                    // 使用 Task.WhenAny 实现超时，防止网络异常时接收循环卡死
                    var receiveTask = _udpClient.ReceiveAsync(_cancellationToken).AsTask();
                    var timeoutTask = Task.Delay(5000, _cancellationToken); // 5秒超时
                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // 超时：检查是否真的卡死（可能是网络问题）
                        // 如果正在停止，直接退出
                        if (_isStopping || _cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        
                        // ✅ 检查流是否已断开（长时间没有收到任何包）
                        if (_isReady)
                        {
                            var timeSinceLastPacket = (DateTime.UtcNow - _lastPacketReceivedTime).TotalSeconds;
                            if (timeSinceLastPacket >= STREAM_TIMEOUT_SECONDS)
                            {
                                _logger.LogError("❌ 流已断开：{Seconds} 秒没有收到任何 UDP 包，触发断开连接", timeSinceLastPacket);
                                
                                // ✅ 触发断开连接回调
                                if (_onDisconnectCallback != null)
                                {
                                    try
                                    {
                                        await _onDisconnectCallback();
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "❌ 断开连接回调执行失败");
                                    }
                                }
                                
                                break; // 退出接收循环
                            }
                            
                            // ✅ 记录超时但继续重试（允许网络临时中断）
                            _logger.LogWarning("UDP receive timeout (5s), continuing to retry... (last packet: {Seconds}s ago)", timeSinceLastPacket);
                        }
                        
                        continue;
                    }
                    
                    // 正常接收到数据
                    if (receiveTask.IsCompletedSuccessfully)
                    {
                        var result = await receiveTask;
                        if (result.Buffer != null && result.Buffer.Length > 0)
                        {
                            // ✅ 更新最后收到包的时间
                            _lastPacketReceivedTime = DateTime.UtcNow;
                            HandleReceivedData(result.Buffer);
                        }
                    }
                    else
                    {
                        // 如果接收任务失败，会由外层 catch 处理
                        await receiveTask;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // UDP 客户端已被释放，退出循环
                    _logger.LogDebug("UDP client disposed, exiting receive loop");
                    break;
                }
                catch (SocketException ex)
                {
                    // ✅ 处理 Socket 异常（网络问题）
                    if (_isStopping)
                    {
                        _logger.LogDebug("Stopping, exiting receive loop");
                        break;
                    }
                    
                    // Socket 错误：等待后重试
                    _logger.LogWarning(ex, "Socket error in receive loop (error={ErrorCode}), retrying in 500ms", ex.SocketErrorCode);
                    await Task.Delay(500, _cancellationToken);
                }
                catch (Exception ex)
                {
                    // 如果正在停止，不再重试
                    if (_isStopping)
                    {
                        _logger.LogDebug("Stopping, exiting receive loop");
                        break;
                    }
                    _logger.LogWarning(ex, "Error in receive loop, retrying in 500ms");
                    await Task.Delay(500, _cancellationToken);
                }
            }
            
            _logger.LogDebug("Receive loop ended");
        }

        /// <summary>
        /// 处理收到的数据
        /// </summary>
        private void HandleReceivedData(byte[] data)
        {
            // 检查是否为 AV 包
            if (data.Length > 0 && Packet.IsAv(data[0]))
            {
                // 处理 AV 包
                if (_avHandler != null && _receiver != null)
                {
                    try
                    {
                        _avHandler.AddPacket(data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error in AddPacket, len={Len}", data.Length);
                    }
                }
                else
                {
                    _logger.LogWarning("Received AV packet but AVHandler or receiver is null (avHandler={AvHandler}, receiver={Receiver})", 
                        _avHandler != null, _receiver != null);
                }
                return;
            }

            // 处理控制包
            HandleControlPacket(data);
        }

        /// <summary>
        /// 处理控制包
        /// </summary>
        private void HandleControlPacket(byte[] data)
        {
            var packet = Packet.Parse(data);
            if (packet == null)
            {
                _logger.LogWarning("Failed to parse control packet, len={Len}", data.Length);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                if (packet.ChunkType == ChunkType.DATA)
                {
                    _logger.LogTrace("UDP recv DATA chunk: channel={Channel}, tsn={Tsn}, dataType=0x{DataType:X2}, len={Len}",
                        packet.Channel,
                        packet.Tsn,
                        packet.DataType ?? 0,
                        data.Length);
                }
                else
                {
                    _logger.LogTrace("UDP recv control chunk: type={ChunkType}, flag={Flag}, len={Len}",
                        packet.ChunkType,
                        packet.Flag,
                        data.Length);
                }
            }

            // 如果 TSN 为 0 或 Data 为空，记录警告
            if (packet.ChunkType == ChunkType.DATA && (packet.Tsn == 0 || (packet.Data?.Length ?? 0) == 0))
            {
                _logger.LogWarning("DATA packet has empty TSN or Data: packetLen={Len}, chunkType={ChunkType}", 
                    data.Length, packet.ChunkType);
            }

            // 验证 GMAC（如果有 cipher）
            if (_cipher != null)
            {
                var gmac = packet.Gmac;
                var keyPos = packet.KeyPos;
                var gmacBytes = BitConverter.GetBytes(gmac);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(gmacBytes);

                // 创建临时副本用于 GMAC 验证
                var tmp = new byte[data.Length];
                Buffer.BlockCopy(data, 0, tmp, 0, data.Length);
                if (tmp.Length >= 13)
                {
                    Array.Clear(tmp, 5, 4);  // 清除 GMAC
                    Array.Clear(tmp, 9, 4);  // 清除 key_pos
                }

                // 验证 GMAC
                // var verified = _cipher.VerifyGmac(tmp, (int)keyPos, gmacBytes);
            }

            // 根据 Chunk 类型分发
            switch (packet.ChunkType)
            {
                case ChunkType.INIT_ACK:
                    HandleInitAck(packet);
                    break;

                case ChunkType.COOKIE_ACK:
                    HandleCookieAck();
                    break;

                case ChunkType.DATA_ACK:
                    HandleDataAck(packet);
                    break;

                case ChunkType.DATA:
                    HandleData(packet, data);
                    break;

                default:
                    break;
            }
        }

        #endregion

        #region Packet Handlers

        /// <summary>
        /// 处理 INIT_ACK
        /// </summary>
        private void HandleInitAck(Packet packet)
        {
            _tagRemote = packet.Params.Tag;
            var cookieData = packet.Params.Data ?? Array.Empty<byte>();

            _logger.LogInformation("INIT_ACK received: tagRemote={TagRemote}, cookieLen={Len}",
                _tagRemote, cookieData.Length);

            // 发送 COOKIE
            SendCookie(cookieData);
        }

        /// <summary>
        /// 处理 COOKIE_ACK
        /// </summary>
        private void HandleCookieAck()
        {
            _logger.LogInformation("COOKIE_ACK received");

            // 发送 BIG
            SendBig();
        }

        /// <summary>
        /// 处理 DATA_ACK
        /// </summary>
        private void HandleDataAck(Packet packet)
        {
            var tsn = (uint)packet.Params.Tsn;

            // 检查是否有等待的 ACK 回调
            if (_ackCallback != null && _ackCallbackTsn == tsn)
            {
                _ackCallback();
                _ackCallback = null;
                _ackCallbackTsn = 0;
            }
        }

        /// <summary>
        /// 处理 DATA 包
        /// </summary>
        private void HandleData(Packet packet, byte[] originalData)
        {
            // 注意：DATA 包的 TSN 和 Data 存储在 packet.Tsn 和 packet.Data，不是 packet.Params
            var tsn = packet.Tsn;

            // 检查重复包
            if (IsDuplicateTsn(tsn))
            {
                return;
            }

            MarkTsnAsProcessed(tsn);

            // 发送 DATA_ACK
            SendDataAck(tsn);

			// 处理 Takion 消息
			if (packet.Data == null || packet.Data.Length == 0)
			{
				_logger.LogWarning(
					"Received DATA packet with empty payload: tsn={Tsn}, dataType={DataType}",
					tsn,
					packet.DataType?.ToString("X2") ?? "null");
				return;
			}

			DispatchTakionData(packet);
        }

		/// <summary>
		/// 根据数据类型分发 Takion DATA 消息。
		/// </summary>
		private void DispatchTakionData(Packet packet)
		{
			var payload = packet.Data ?? Array.Empty<byte>();
			if (payload.Length == 0)
			{
				if (_logger.IsEnabled(LogLevel.Trace))
				{
					_logger.LogTrace("Takion data ignored: empty payload, type={DataType}", packet.DataType ?? 0);
				}
				return;
			}

			var dataType = (TakionDataType)(packet.DataType ?? (byte)TakionDataType.Protobuf);
			switch (dataType)
			{
				case TakionDataType.Protobuf:
					ProcessTakionMessage(payload);
					break;
				case TakionDataType.Rumble:
					HandleRumble(payload);
					break;
				case TakionDataType.PadInfo:
					HandlePadInfo(payload);
					break;
				case TakionDataType.TriggerEffects:
					HandleTriggerEffects(payload);
					break;
				default:
					if (_logger.IsEnabled(LogLevel.Trace))
					{
						_logger.LogTrace("Unhandled Takion data type {DataType}, length={Length}", (byte)dataType, payload.Length);
					}
					break;
			}
		}

		/// <summary>
		/// 处理 Takion 消息
		/// </summary>
		private void ProcessTakionMessage(byte[] data)
        {
            if (!ProtoCodec.TryParse(data, out var message))
            {
                _logger.LogWarning("Failed to parse Takion message, len={Len}", data.Length);
                return;
            }

            switch (message.Type)
            {
                case Protos.TakionMessage.Types.PayloadType.Bang:
                    HandleBang(message);
                    break;

                case Protos.TakionMessage.Types.PayloadType.Streaminfo:
                    HandleStreamInfo(message);
                    break;

                case Protos.TakionMessage.Types.PayloadType.Streaminfoack:
                    break;

                case Protos.TakionMessage.Types.PayloadType.Heartbeat:
                    // ✅ 收到心跳时立即回复
                    // 这可以确保 PlayStation 知道我们仍然活跃并在线
                    if (_cipher != null)
                    {
                        try
                        {
                            var heartbeatReply = ProtoCodec.BuildHeartbeat();
                            SendData(heartbeatReply, channel: 1, flag: 1, proto: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send heartbeat reply");
                        }
                    }
                    break;

                case Protos.TakionMessage.Types.PayloadType.Disconnect:
                    // ✅ 如果正在进行流重置，忽略 Disconnect 消息（不释放 session）
                    // 因为流重置期间可能会收到 Disconnect，但这是正常的，不应该释放 session
                    if (_isReconnecting)
                    {
                        _logger.LogInformation("DISCONNECT received during stream reconnection, ignoring (session preserved)");
                        break;
                    }
                    _logger.LogWarning("DISCONNECT received from PS5, handling disconnect...");
                    _ = Task.Run(async () => await HandleDisconnectAsync());
                    break;

                case Protos.TakionMessage.Types.PayloadType.Connectionquality:
                    // ✅ 处理连接质量消息
                    // PS5 发送的质量信息，包含目标码率、上行码率、丢失率、RTT 等
                    HandleConnectionQuality(message);
                    break;

                default:
                    break;
            }
        }

		private void HandleRumble(byte[] data)
		{
			if (data.Length < 3)
			{
				_logger.LogWarning("Rumble payload too short: len={Length}", data.Length);
				return;
			}

			double multiplier;
			int ps5RumbleIntensity;
			int ps5TriggerIntensity;
			lock (_rumbleLock)
			{
				multiplier = _rumbleMultiplier;
				ps5RumbleIntensity = _ps5RumbleIntensity;
				ps5TriggerIntensity = _ps5TriggerIntensity;
			}

			if (ps5RumbleIntensity < 0)
			{
				if (_logger.IsEnabled(LogLevel.Trace))
				{
					_logger.LogTrace("Skipping rumble packet because haptics are disabled.");
				}
				return;
			}

			byte unknown = data[0];
			byte left = data[1];
			byte right = data[2];

			var leftScaled = (int)(left * multiplier);
			var rightScaled = (int)(right * multiplier);

			byte adjustedLeft = (byte)Math.Clamp(leftScaled, 0, 255);
			byte adjustedRight = (byte)Math.Clamp(rightScaled, 0, 255);

			if (_logger.IsEnabled(LogLevel.Trace))
			{
				_logger.LogTrace(
					"Rumble packet: unknown={Unknown}, left={Left}, right={Right}, adjustedLeft={AdjustedLeft}, adjustedRight={AdjustedRight}, multiplier={Multiplier:F2}",
					unknown, left, right, adjustedLeft, adjustedRight, multiplier);
			}

			OnRumbleReceived(new RumbleEventArgs(
				unknown,
				left,
				right,
				adjustedLeft,
				adjustedRight,
				multiplier,
				ps5RumbleIntensity,
				ps5TriggerIntensity));
		}

		private void HandlePadInfo(byte[] data)
		{
			ReadOnlySpan<byte> ledSpan = default;
			byte? newPlayerIndex = null;
			bool motionReset = false;

			if (data.Length == 0x19)
			{
				byte haptic = data[20];
				byte trigger = data[21];
				ApplyHapticIntensity(haptic);
				ApplyTriggerIntensity(trigger);
				motionReset = data[12] != 0;
				newPlayerIndex = data[8];
				ledSpan = data.AsSpan(9, 3);
			}
			else if (data.Length == 0x11)
			{
				byte haptic = data[12];
				byte trigger = data[13];
				ApplyHapticIntensity(haptic);
				ApplyTriggerIntensity(trigger);
				motionReset = data[4] != 0;
				newPlayerIndex = data[0];
				ledSpan = data.AsSpan(1, 3);
			}
			else
			{
				if (_logger.IsEnabled(LogLevel.Debug))
				{
					_logger.LogDebug("Unexpected pad info payload length={Length}", data.Length);
				}
				return;
			}

			bool ledChanged = false;
			byte? playerIndexChangedTo = null;
			if (!ledSpan.IsEmpty || newPlayerIndex.HasValue)
			{
				lock (_rumbleLock)
				{
					if (newPlayerIndex.HasValue && newPlayerIndex.Value != _playerIndex)
					{
						_playerIndex = newPlayerIndex.Value;
						playerIndexChangedTo = _playerIndex;
					}

					if (!ledSpan.IsEmpty && !ledSpan.SequenceEqual(_ledState))
					{
						ledSpan.CopyTo(_ledState);
						ledChanged = true;
					}
				}
			}

			if (motionReset && _logger.IsEnabled(LogLevel.Debug))
			{
				_logger.LogDebug("Pad info requested motion reset.");
			}

			if (playerIndexChangedTo.HasValue && _logger.IsEnabled(LogLevel.Debug))
			{
				_logger.LogDebug("Player index updated to {PlayerIndex}", playerIndexChangedTo.Value);
			}

			if (ledChanged && _logger.IsEnabled(LogLevel.Trace))
			{
				_logger.LogTrace("LED state updated to {Led}", BitConverter.ToString(_ledState));
			}
		}

		private void HandleTriggerEffects(byte[] data)
		{
			int triggerIntensity;
			lock (_rumbleLock)
			{
				triggerIntensity = _ps5TriggerIntensity;
			}

			if (triggerIntensity < 0)
			{
				if (_logger.IsEnabled(LogLevel.Trace))
				{
					_logger.LogTrace("Trigger effects ignored because trigger intensity is disabled.");
				}
				return;
			}

			if (data.Length < 25)
			{
				_logger.LogWarning("Trigger effects payload too short: len={Length}", data.Length);
				return;
			}

			if (_logger.IsEnabled(LogLevel.Trace))
			{
				_logger.LogTrace("Trigger effects payload: {Payload}", BitConverter.ToString(data));
			}
		}

		private void ApplyHapticIntensity(byte intensityCode)
		{
			bool changed = false;
			lock (_rumbleLock)
			{
				if (_currentHapticIntensityCode == intensityCode)
				{
					return;
				}
				_currentHapticIntensityCode = intensityCode;
				changed = true;

				switch (intensityCode)
				{
					case 0:
						_ps5RumbleIntensity = -1;
						_rumbleMultiplier = 0.0;
						break;
					case 1:
						_ps5RumbleIntensity = 0x00;
						_rumbleMultiplier = 1.0;
						break;
					case 2:
						_ps5RumbleIntensity = 0x02;
						_rumbleMultiplier = DUALSENSE_MEDIUM_MULTIPLIER;
						break;
					case 3:
						_ps5RumbleIntensity = 0x03;
						_rumbleMultiplier = DUALSENSE_WEAK_MULTIPLIER;
						break;
					default:
						_ps5RumbleIntensity = 0x00;
						_rumbleMultiplier = 1.0;
						break;
				}
			}

			if (changed && _logger.IsEnabled(LogLevel.Debug))
			{
				_logger.LogDebug(
					"Haptic intensity updated: code={Code}, ps5={Ps5}, multiplier={Multiplier:F2}",
					intensityCode,
					_ps5RumbleIntensity,
					_rumbleMultiplier);
			}
		}

		private void ApplyTriggerIntensity(byte intensityCode)
		{
			bool changed = false;
			lock (_rumbleLock)
			{
				if (_currentTriggerIntensityCode == intensityCode)
				{
					return;
				}
				_currentTriggerIntensityCode = intensityCode;
				changed = true;

				switch (intensityCode)
				{
					case 0:
						_ps5TriggerIntensity = -1;
						break;
					case 1:
						_ps5TriggerIntensity = 0x00;
						break;
					case 2:
						_ps5TriggerIntensity = 0x60;
						break;
					case 3:
						_ps5TriggerIntensity = 0x90;
						break;
					default:
						_ps5TriggerIntensity = 0x00;
						break;
				}
			}

			if (changed && _logger.IsEnabled(LogLevel.Trace))
			{
				_logger.LogTrace(
					"Trigger intensity updated: code={Code}, ps5={Ps5}",
					intensityCode,
					_ps5TriggerIntensity);
			}
		}

		private void OnRumbleReceived(RumbleEventArgs args)
		{
			try
			{
				RumbleReceived?.Invoke(this, args);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error while notifying rumble listeners");
			}
		}

        /// <summary>
        /// 处理断开连接
        /// </summary>
        private async Task HandleDisconnectAsync()
        {
            try
            {
                _logger.LogWarning("Handling PS5 disconnect: stopping stream and session...");
                
                // 先触发断开连接回调（由 StreamingService 处理 session 停止和客户端通知）
                // 注意：回调应该在停止流之前调用，以便 StreamingService 可以正确处理
                if (_onDisconnectCallback != null)
                {
                    await _onDisconnectCallback();
                }
                
                // 然后停止流（清理资源）
                await StopAsync();
                
                _logger.LogInformation("PS5 disconnect handled successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling PS5 disconnect");
            }
        }

        /// <summary>
        /// 处理 BANG 消息
        /// </summary>
        private void HandleBang(Protos.TakionMessage message)
        {
            var bangPayload = message.BangPayload;
            if (bangPayload == null)
            {
                _logger.LogError("BANG payload is null");
                return;
            }

            _logger.LogInformation("BANG received: version={Version}, accepted={Accepted}",
                bangPayload.ServerVersion, bangPayload.VersionAccepted);

            if (!bangPayload.VersionAccepted)
            {
                _logger.LogError("RP Big Payload not accepted");
                return;
            }

            // 设置加密
            var ecdhPub = bangPayload.EcdhPubKey?.ToByteArray() ?? Array.Empty<byte>();
            var ecdhSig = bangPayload.EcdhSig?.ToByteArray() ?? Array.Empty<byte>();

            if (!SetCiphers(ecdhPub, ecdhSig))
            {
                _logger.LogError("Failed to set ciphers");
                return;
            }

            // 如果已有接收器，设置 cipher
            if (_receiver != null && _avHandler != null)
            {
                _avHandler.SetCipher(_cipher!);
            }

            // ✅ 启动 FeedbackSender 和 CongestionControl 服务
            // PS5 需要收到 Feedback 才会开始发送视频流
            StartFeedbackAndCongestionServices();

            // 设置就绪状态
            SetReady();

            // ✅ 如果之前有挂起的 IDR 请求，则在 cipher 就绪后立即发送一次
            if (_idrPending)
            {
                _idrPending = false;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100, _cancellationToken);
                        await SendIdrRequestAsync();
                    }
                    catch
                    {
                        // ignore
                    }
                }, _cancellationToken);
            }
        }
        
        /// <summary>
        /// 启动 Feedback 和 Congestion 服务
        /// </summary>
        private void StartFeedbackAndCongestionServices()
        {
            try
            {
                // 启动 FeedbackSender（200ms 心跳）
                _feedbackSender?.Start();
                
                // 启动 CongestionControl（66ms 间隔）
                _congestionControl?.Start();
                
                // ✅ 启动周期性 IDR 请求器
                // 目的：确保定期获得关键帧，避免长时间 P 帧累积导致的恢复困难
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500, _cancellationToken); // 等待服务稳定
                        if (!_cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("🎬 启动 IDR 请求循环");
                            await StartIdrRequesterAsync();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IDR 请求循环启动失败");
                    }
                }, _cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Feedback/Congestion services");
            }
        }
        
        /// <summary>
        /// 发送 IDR 请求（请求 PS5 发送关键帧）
        /// ✅ 公共方法：允许外部（如 WebRTCReceiver）请求关键帧
        /// </summary>
        public async Task RequestKeyframeAsync()
        {
            // ✅ 添加冷却机制：防止频繁请求关键帧（PS5 可能忽略过于频繁的请求）
            var now = DateTime.UtcNow;
            if (_lastKeyframeRequest != DateTime.MinValue && 
                (now - _lastKeyframeRequest) < _keyframeRequestCooldown)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("⏱️ 关键帧请求冷却中，忽略请求（距离上次请求 {Elapsed}ms < {Cooldown}ms）",
                        (now - _lastKeyframeRequest).TotalMilliseconds, _keyframeRequestCooldown.TotalMilliseconds);
                }
                return;
            }
            
            await SendIdrRequestAsync();
        }
        
        /// <summary>
        /// 发送 IDR 请求（请求 PS5 发送关键帧）
        /// </summary>
        private async Task SendIdrRequestAsync()
        {
            try
            {
                // ✅ 检查前置条件：必须有 cipher（GMAC 需要）
                if (_cipher == null)
                {
                    // 将 IDR 请求标记为挂起，待 cipher 初始化后立即发送一次
                    _idrPending = true;
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("⏱️ IDR 请求已挂起：cipher 未初始化");
                    }
                    return;
                }
                
                var idr = ProtoCodec.BuildIdrRequest();
                
                // 验证消息长度（应该只有 type 字段，约 2-3 字节）
                if (idr.Length < 2 || idr.Length > 10)
                {
                    _logger.LogError("IDRREQUEST message length invalid: {Len} bytes", idr.Length);
                }
                
                // ✅ 发送 IDRREQUEST（使用 GMAC 但不加密 payload）
                // 使用 SendData 方法，flag=1, channel=1, proto=false
                SendData(idr, flag: 1, channel: 1, proto: false);
                RecordIdrRequest();
                
                _logger.LogDebug("📤 IDR 请求已发送到 PS5");
                
                await Task.CompletedTask;  // 保持异步方法签名
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send IDRREQUEST");
            }
        }

        private void RecordIdrRequest()
        {
            var now = DateTime.UtcNow;
            int total;
            int recent;
            int windowSeconds = (int)_idrMetricsWindow.TotalSeconds;

            lock (_idrMetricsLock)
            {
                _totalIdrRequests++;
                _lastKeyframeRequest = now;
                _idrRequestHistory.Enqueue(now);
                TrimIdrRequestHistory_NoLock(now);
                total = _totalIdrRequests;
                recent = _idrRequestHistory.Count;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("IDR request recorded. total={Total}, recent({Window}s)={Recent}", total, windowSeconds, recent);
            }
        }

        private void TrimIdrRequestHistory_NoLock(DateTime now)
        {
            while (_idrRequestHistory.Count > 0 && now - _idrRequestHistory.Peek() > _idrMetricsWindow)
                _idrRequestHistory.Dequeue();
        }

        private (int Total, int Recent) GetIdrRequestMetrics()
        {
            lock (_idrMetricsLock)
            {
                var now = DateTime.UtcNow;
                TrimIdrRequestHistory_NoLock(now);
                return (_totalIdrRequests, _idrRequestHistory.Count);
            }
        }

        /// <summary>
        /// 定期发送 IDRREQUEST，确保视频流稳定
        /// </summary>
        private async Task StartIdrRequesterAsync()
        {
            try
            {
                // 阶段1: 初始连接 - 连续发送 5 次确保收到 IDR 帧
                for (int i = 0; i < 5; i++)
                {
                    if (_cancellationToken.IsCancellationRequested) 
                        break;
                    
                    await SendIdrRequestAsync();
                    await Task.Delay(500, _cancellationToken);
                }
                
                // 阶段2: 定期维护 - 每 2 秒发送一次
                // 频率说明：
                // - HLS 配置 -hls_time 1（1秒分片）需要频繁的关键帧
                // - 2 秒间隔确保每 1-2 个分片有一个关键帧
                // - 既满足 HLS 低延迟需求，又不会过度请求
                while (!_cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(2000, _cancellationToken); // 2 秒间隔
                    
                    if (_cancellationToken.IsCancellationRequested) 
                        break;
                    
                    await SendIdrRequestAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，无需记录
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IDR requester error");
            }
        }
        
        /// <summary>
        /// 发送 Feedback 包的回调
        /// </summary>
        private async Task SendFeedbackPacketAsync(int type, ushort sequence, byte[] data)
        {
            // 如果正在停止，直接返回
            if (_isStopping || _cipher == null)
            {
                await Task.CompletedTask;
                return;
            }
            
            SendFeedback(type, sequence, data);
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// 发送原始包的回调
        /// 注意：对于拥塞控制包（类型 0x05），需要使用专门的处理逻辑
        /// </summary>
        private async Task SendRawPacketAsync(byte[] packet)
        {
            // 如果正在停止，直接返回
            if (_isStopping || _cipher == null)
            {
                await Task.CompletedTask;
                return;
            }
            
            // 检查是否为拥塞控制包（类型 0x05，大小 15 字节）
            if (packet.Length == 15 && packet.Length > 0 && (packet[0] & 0x0F) == 0x05)
            {
                // 拥塞控制包需要特殊处理
                SendCongestionControlPacket(packet);
            }
            else
            {
                // 其他包使用标准处理
                SendRaw(packet);
            }
            
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// 获取当前 key_pos 的回调
        /// </summary>
        private ulong GetCurrentKeyPos()
        {
            return (ulong)(_cipher?.KeyPos ?? 0);
        }
        
        /// <summary>
        /// 获取包统计（用于 CongestionControl）
        /// </summary>
        private (ushort, ushort) GetPacketStats()
        {
            if (_avHandler == null)
            return (0, 0);

            var stats = _avHandler.GetAndResetStats();
            _lastPipelineStats = stats;

            int totalReceived = stats.VideoReceived + stats.AudioReceived;
            int totalLost = stats.VideoLost + stats.AudioLost;

            if (totalReceived < 0) totalReceived = 0;
            if (totalLost < 0) totalLost = 0;
            if (totalReceived > ushort.MaxValue) totalReceived = ushort.MaxValue;
            if (totalLost > ushort.MaxValue) totalLost = ushort.MaxValue;
            return ((ushort)totalReceived, (ushort)totalLost);
        }

        public (StreamHealthSnapshot Snapshot, StreamPipelineStats PipelineStats) GetStreamHealth()
        {
            // ✅ 使用缓存的快照作为默认值，避免阻塞
            StreamHealthSnapshot snapshot = _healthSnapshot;
            
            // ✅ 尝试获取最新快照，但如果失败或超时，使用缓存
            if (_avHandler != null)
            {
                try
                {
                    snapshot = _avHandler.GetHealthSnapshot(resetDeltas: true);
                    _healthSnapshot = snapshot; // 更新缓存
                }
                catch (Exception ex)
                {
                    // 如果获取失败，使用缓存的快照（避免阻塞 health 接口）
                    _logger.LogWarning(ex, "⚠️ Failed to get health snapshot, using cached snapshot");
                }
            }

            StreamPipelineStats pipeline = _lastPipelineStats;
            if (_avHandler != null)
            {
                try
                {
                    var (totalIdr, recentIdr) = GetIdrRequestMetrics();
                    pipeline = pipeline with
                    {
                        TotalIdrRequests = totalIdr,
                        IdrRequestsRecent = recentIdr,
                        IdrRequestWindowSeconds = (int)_idrMetricsWindow.TotalSeconds,
                        LastIdrRequestUtc = _lastKeyframeRequest == DateTime.MinValue ? null : _lastKeyframeRequest,
                        FrameOutputFps = snapshot.RecentFps,
                        FrameIntervalMs = snapshot.AverageFrameIntervalMs
                    };
                    if (pipeline.FecAttempts > 0 && pipeline.FecSuccessRate <= 0)
                    {
                        pipeline = pipeline with
                        {
                            FecSuccessRate = (double)pipeline.FecSuccess / pipeline.FecAttempts
                        };
                    }
                    _lastPipelineStats = pipeline;
                }
                catch (Exception ex)
                {
                    // 如果获取统计失败，使用缓存的统计（避免阻塞）
                    _logger.LogWarning(ex, "⚠️ Failed to get pipeline stats, using cached stats");
                }
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace(
                    "StreamHealth: fps={Fps:F2}, ΔFrozen={DeltaFrozen}, ΔRecovered={DeltaRecovered}, ΔDropped={DeltaDropped}, FEC={FecSuccess}/{FecAttempts}({FecRate:P1}), pending={Pending}, IDR_recent={IdrRecent}",
                    snapshot.RecentFps,
                    snapshot.DeltaFrozenFrames,
                    snapshot.DeltaRecoveredFrames,
                    snapshot.DeltaDroppedFrames,
                    pipeline.FecSuccess,
                    pipeline.FecAttempts,
                    pipeline.FecSuccessRate,
                    pipeline.PendingPackets,
                    pipeline.IdrRequestsRecent);
            }

            return (snapshot, pipeline);
        }

        private void OnStreamHealthEvent(StreamHealthEvent evt)
        {
            _healthSnapshot = _avHandler?.GetHealthSnapshot() ?? new StreamHealthSnapshot
            {
                Timestamp = evt.Timestamp,
                LastStatus = evt.Status,
                Message = evt.Message,
                ConsecutiveFailures = evt.ConsecutiveFailures
            };

            // ✅ 处理流健康事件
            if (evt.Status == FrameProcessStatus.Success || evt.Status == FrameProcessStatus.Recovered)
            {
                // ✅ 增加连续成功帧数
                _consecutiveSuccessFrames++;
                
                // ✅ 流健康恢复：需要满足多个条件才认为恢复（避免误判）
                // 1. 连续成功帧数 >= 阈值
                // 2. 在 fallback 后至少处理了足够的新帧（frame index > fallback frame index + 阈值）
                // 3. 距离最后一次 fallback 至少过了最小恢复时间
                bool hasRecoveryFrames = _lastFallbackFrameIndex < 0 || 
                    (evt.FrameIndex > _lastFallbackFrameIndex + RECOVERY_FRAME_INDEX_THRESHOLD);
                bool hasRecoveryDuration = _lastFallbackTime == DateTime.MinValue || 
                    (DateTime.UtcNow - _lastFallbackTime) >= RECOVERY_MIN_DURATION;
                
                if (_consecutiveSevereFailures > 0 && 
                    _consecutiveSuccessFrames >= RECOVERY_SUCCESS_THRESHOLD &&
                    hasRecoveryFrames && 
                    hasRecoveryDuration)
                {
                    // ✅ 流真正恢复
                    _logger.LogInformation("✅ Stream health recovered (consecutive success={Success}, frame={Frame}, fallback frame={FallbackFrame}, duration={Duration}ms)", 
                        _consecutiveSuccessFrames, evt.FrameIndex, _lastFallbackFrameIndex, 
                        _lastFallbackTime != DateTime.MinValue ? (DateTime.UtcNow - _lastFallbackTime).TotalMilliseconds : 0);
                    
                    // ✅ 恢复后主动请求关键帧，确保流真正恢复
                    if (DateTime.UtcNow - _lastKeyframeRequest > _keyframeRequestCooldown)
                    {
                        _ = RequestKeyframeAsync();
                    }
                    
                    _consecutiveSevereFailures = 0;
                    _consecutiveSuccessFrames = 0; // 重置连续成功计数
                    _lastFallbackFrameIndex = -1; // 重置 fallback 帧索引
                    _lastFallbackTime = DateTime.MinValue; // 重置 fallback 时间
                }
                else if (_consecutiveSevereFailures > 0)
                {
                    // ✅ 部分恢复：记录但需要更多成功帧或时间
                    if (_consecutiveSuccessFrames % 5 == 0) // 每 5 帧记录一次，避免日志过多
                    {
                        _logger.LogDebug("Stream health improving (consecutive success={Success}/{Threshold}, frame={Frame}, fallback frame={FallbackFrame}, has frames={HasFrames}, has duration={HasDuration})", 
                            _consecutiveSuccessFrames, RECOVERY_SUCCESS_THRESHOLD, evt.FrameIndex, _lastFallbackFrameIndex, 
                            hasRecoveryFrames, hasRecoveryDuration);
                    }
                }
                else
                {
                    // ✅ 流正常：重置连续成功计数和 fallback 信息
                    _consecutiveSuccessFrames = 0;
                    _lastFallbackFrameIndex = -1;
                    _lastFallbackTime = DateTime.MinValue;
                }
                return;
            }

            if (evt.Status is FrameProcessStatus.Frozen or FrameProcessStatus.Dropped)
            {
                // ✅ 失败帧：重置连续成功计数，增加连续失败计数
                _consecutiveSuccessFrames = 0;
                _consecutiveSevereFailures = evt.ConsecutiveFailures;
                
                // ✅ 记录 fallback 信息（用于判断是否真正恢复）
                // 所有 Frozen/Dropped 状态都可能是由 fallback 触发的，记录帧索引和时间
                _lastFallbackFrameIndex = evt.FrameIndex;
                _lastFallbackTime = DateTime.UtcNow;
                
                // ✅ 轻度恢复：连续失败 2-4 次时触发快速恢复（不重建连接）
                if (_consecutiveSevereFailures >= 2 && _consecutiveSevereFailures < 5)
                {
                    _ = TriggerLightRecoveryAsync(evt);
                }
            }
        }

        /// <summary>
        /// 轻度恢复（快速恢复，不重建连接）
        /// </summary>
        private async Task TriggerLightRecoveryAsync(StreamHealthEvent evt)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDegradeAction < TimeSpan.FromSeconds(5))
                return;

            _lastDegradeAction = now;
            _logger.LogWarning("⚠️ Stream degradation detected. Frame={Frame}, status={Status}, consecutive={Consecutive}", evt.FrameIndex, evt.Status, evt.ConsecutiveFailures);

            // ✅ 发送 corrupt 报告和请求关键帧（恢复机制，不是主动降档）
            if (evt.FrameIndex > 0)
                SendCorrupt(evt.FrameIndex, evt.FrameIndex);

            if (DateTime.UtcNow - _lastKeyframeRequest > _keyframeRequestCooldown)
            {
                await RequestKeyframeAsync();
            }
        }

        /// <summary>
        /// 设置加密
        /// </summary>
        private bool SetCiphers(byte[] ecdhPub, byte[] ecdhSig)
        {
            if (_ecdh == null)
            {
                _logger.LogError("ECDH is null");
                return false;
            }

            if (!_ecdh.SetSecret(ecdhPub, ecdhSig, out var secret))
            {
                _logger.LogError("ECDH verification failed");
                return false;
            }

            _cipher = new StreamCipher(_ecdh.HandshakeKey, secret);
            _logger.LogInformation("Ciphers set successfully, keyPos={KeyPos}", _cipher.KeyPos);
            return true;
        }

        /// <summary>
        /// 处理 STREAMINFO 消息
        /// </summary>
        private void HandleStreamInfo(Protos.TakionMessage message)
        {
            _logger.LogInformation("STREAMINFO received");

            var streamInfo = message.StreamInfoPayload;
            if (streamInfo == null)
            {
                _logger.LogError("StreamInfo payload is null");
                return;
            }

            // ✅ 解析所有 resolutions 并设置到 AdaptiveStreamManager
            var profiles = new List<VideoProfile>();
            if (streamInfo.Resolution != null && streamInfo.Resolution.Count > 0)
            {
                for (int i = 0; i < streamInfo.Resolution.Count; i++)
                {
                    var resolution = streamInfo.Resolution[i];
                    var header = resolution.VideoHeader?.ToByteArray() ?? Array.Empty<byte>();
                    if (header.Length > 0)
                    {
                        var profile = new VideoProfile(i, (int)resolution.Width, (int)resolution.Height, header);
                        profiles.Add(profile);
                    }
                }
            }

            // 设置到 AdaptiveStreamManager
            if (_adaptiveStreamManager != null && profiles.Count > 0)
            {
                _adaptiveStreamManager.SetProfiles(profiles);
                
                // ✅ 诊断：检查是否有多个 profiles（PS5 降档需要多个 profiles）
                if (profiles.Count == 1)
                {
                    _logger.LogWarning("⚠️ 警告：只收到 1 个 profile ({Width}x{Height})，PS5 无法降档！" +
                        " 如果网络变差，PS5 无法切换到更低的分辨率。", 
                        profiles[0].Width, profiles[0].Height);
                }
                else
                {
                    _logger.LogInformation("✅ 收到 {Count} 个 profiles，PS5 可以根据网络状况进行降档/升档", profiles.Count);
                }
            }
            else if (profiles.Count == 0)
            {
                _logger.LogWarning("⚠️ 警告：没有收到任何 profiles，无法进行自适应流管理！");
            }

            // 提取第一个视频和音频头（用于向后兼容）
            var rawVideoHeader = profiles.Count > 0 ? profiles[0].Header : Array.Empty<byte>();
            var audioHeader = streamInfo.AudioHeader?.ToByteArray() ?? Array.Empty<byte>();

            // 视频 header 需要添加 FFMPEG_PADDING（64字节）
            // AVStream 在构造时会添加 padding，然后在第一帧或 OnStreamInfo 中发送
            byte[] videoHeader = rawVideoHeader;
            if (rawVideoHeader.Length > 0)
            {
                var padding = new byte[64];
                var paddedHeader = new byte[rawVideoHeader.Length + padding.Length];
                System.Buffer.BlockCopy(rawVideoHeader, 0, paddedHeader, 0, rawVideoHeader.Length);
                System.Buffer.BlockCopy(padding, 0, paddedHeader, rawVideoHeader.Length, padding.Length);
                videoHeader = paddedHeader;
            }

            // 缓存 headers（用于后续附加的接收器）- 缓存原始 header，因为 AVHandler 会在内部添加 padding
            _cachedVideoHeader = rawVideoHeader;
            _cachedAudioHeader = audioHeader;

            // 设置 AVHandler 的 headers
            // AVHandler 内部会创建 AVStream，AVStream 会为视频 header 添加 padding
            if (_avHandler != null)
            {
                _avHandler.SetHeaders(rawVideoHeader, audioHeader, _loggerFactory);
            }

            // 通知接收器
            // 发送带 padding 的 header 给 receiver
            if (_receiver != null)
            {
                _receiver.OnStreamInfo(videoHeader, audioHeader);
            }

            // 立即发送 STREAMINFOACK
            // ✅ 修复：不要在这里调用 AdvanceSequence()，SendData 内部会根据 cipher 状态自动处理
            var streamInfoAck = ProtoCodec.BuildStreamInfoAck();
            SendData(streamInfoAck, channel: 9, flag: 1, proto: true);
            
            // ✅ 发送 CONTROLLER_CONNECTION
            // 旧版 RPStream 中存在该逻辑，某些固件可能仍依赖
            SendControllerConnection();
            
            // ✅ 设置就绪状态
            SetReady();
            
            // ✅ 启动兜底：STREAMINFO 后延迟 200–300ms 主动请求一次关键帧
            // 目的：确保启动阶段快速获得首个 IDR，避免初期黑屏/冻结
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250, _cancellationToken);
                    if (!_cancellationToken.IsCancellationRequested)
                    {
                        await RequestKeyframeAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Initial IDR request fallback failed");
                }
            }, _cancellationToken);
        }

        /// <summary>
        /// 处理连接质量消息
        /// PS5 发送的质量信息，用于监控网络状况和帮助诊断降档问题
        /// </summary>
        private void HandleConnectionQuality(Protos.TakionMessage message)
        {
            var quality = message.ConnectionQualityPayload;
            if (quality == null)
            {
                _logger.LogWarning("⚠️ ConnectionQuality payload is null");
                return;
            }

            // ✅ 计算实际测量的码率
            double measuredBitrateMbps = 0;
            if (_avHandler != null)
            {
                // 从 AVHandler 获取健康快照，其中包含流统计信息
                var healthSnapshot = _avHandler.GetHealthSnapshot(resetDeltas: false, resetStreamStats: false);
                // StreamHealthSnapshot 是值类型（record struct），不会为 null
                measuredBitrateMbps = healthSnapshot.MeasuredBitrateMbps;
            }

            // ✅ 记录质量信息
            // 注意：protobuf 生成的字段不是可空类型，使用 HasXxx 检查是否设置，直接使用字段值（有默认值）
            _logger.LogInformation(
                "📊 Connection Quality: target_bitrate={TargetBitrate} kbps, " +
                "upstream_bitrate={UpstreamBitrate} kbps, upstream_loss={UpstreamLoss:P2}, " +
                "disable_upstream_audio={DisableAudio}, rtt={Rtt:F2} ms, loss={Loss}, " +
                "measured_bitrate={MeasuredBitrate:F2} Mbps",
                quality.HasTargetBitrate ? quality.TargetBitrate : 0u,
                quality.HasUpstreamBitrate ? quality.UpstreamBitrate : 0u,
                quality.HasUpstreamLoss ? quality.UpstreamLoss : 0f,
                quality.HasDisableUpstreamAudio ? quality.DisableUpstreamAudio : false,
                quality.HasRtt ? quality.Rtt : 0.0,
                quality.HasLoss ? quality.Loss : 0ul,
                measuredBitrateMbps);

            // ✅ 诊断：检查 PS5 的质量评估和条件
            var upstreamLoss = quality.HasUpstreamLoss ? quality.UpstreamLoss : 0f;
            var rtt = quality.HasRtt ? quality.Rtt : 0.0;
            var targetBitrate = quality.HasTargetBitrate ? quality.TargetBitrate : 0u;
            var upstreamBitrate = quality.HasUpstreamBitrate ? quality.UpstreamBitrate : 0u;
            
            // 检查是否有多个 profiles（降档的前提条件）
            int profileCount = _adaptiveStreamManager?.ProfileCount ?? 0;
            
            if (upstreamLoss > 0.1 || rtt > 100 || (targetBitrate > 0 && upstreamBitrate > 0 && upstreamBitrate < targetBitrate * 0.5))
            {
                _logger.LogWarning(
                    "⚠️ 网络状况较差，但未检测到降档。诊断信息:\n" +
                    "  - 上行丢失率: {Loss:P2}\n" +
                    "  - RTT: {Rtt:F2} ms\n" +
                    "  - 目标码率: {TargetBitrate} kbps\n" +
                    "  - 实际上行码率: {UpstreamBitrate} kbps\n" +
                    "  - Profiles 数量: {ProfileCount}\n" +
                    "可能原因:\n" +
                    "  1) 只有 1 个 profile，PS5 无法降档\n" +
                    "  2) PS5 需要持续的高丢失率（可能需要几秒）\n" +
                    "  3) PS5 可能还考虑其他因素（延迟、带宽趋势等）\n" +
                    "  4) 拥塞控制报告的丢失率可能不够高（当前限制已移除，报告真实丢失率）",
                    upstreamLoss, rtt, targetBitrate, upstreamBitrate, profileCount);
            }
        }

        /// <summary>
        /// Profile 切换回调 - 当检测到 adaptive_stream_index 变化时调用
        /// </summary>
        private void OnProfileSwitched(VideoProfile newProfile, VideoProfile? oldProfile)
        {
            if (_receiver == null || newProfile == null)
                return;

            try
            {
                var isDegradation = false;
                
                if (oldProfile != null)
                {
                    // ✅ 判断是降档还是升档（通过分辨率比较）
                    var oldPixels = oldProfile.Width * oldProfile.Height;
                    var newPixels = newProfile.Width * newProfile.Height;
                    isDegradation = newPixels < oldPixels;
                    
                    if (isDegradation)
                    {
                        _logger.LogWarning("📉 Quality degradation detected: {OldW}x{OldH} -> {NewW}x{NewH} (Profile {OldIndex} -> {NewIndex})", 
                            oldProfile.Width, oldProfile.Height, newProfile.Width, newProfile.Height, oldProfile.Index, newProfile.Index);
                    }
                    else
                    {
                        _logger.LogInformation("📈 Quality upgrade: {OldW}x{OldH} -> {NewW}x{NewH} (Profile {OldIndex} -> {NewIndex})", 
                            oldProfile.Width, oldProfile.Height, newProfile.Width, newProfile.Height, oldProfile.Index, newProfile.Index);
                    }
                }
                else
                {
                    _logger.LogInformation("🔄 Profile 切换: {Width}x{Height} (Profile {Index}), 更新 receiver header", 
                        newProfile.Width, newProfile.Height, newProfile.Index);
                }
                
                // 更新 receiver 的 header（带 padding）
                _receiver.OnStreamInfo(newProfile.HeaderWithPadding, _cachedAudioHeader ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 更新 receiver header 失败");
            }
        }
        
        /// <summary>
        /// 发送 CONTROLLER_CONNECTION
        /// </summary>
        private void SendControllerConnection()
        {
            if (_cipher == null)
            {
                return;
            }
            
            try
            {
                bool isPs5 = _session.HostType.Equals("PS5", StringComparison.OrdinalIgnoreCase);
                var controllerConn = ProtoCodec.BuildControllerConnection(controllerId: 0, isPs5: isPs5);
                SendData(controllerConn, channel: 1, flag: 1, proto: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send CONTROLLER_CONNECTION");
            }
        }

        /// <summary>
        /// 设置就绪状态
        /// </summary>
        private void SetReady()
        {
            // ✅ 只在第一次设置就绪状态时启动心跳循环，避免重复启动
            bool firstTimeReady = !_isReady;
            
            _logger.LogInformation("Stream ready");
            _state = STATE_READY;
            _isReady = true;
            
            // ✅ 启动心跳循环
            // 只在第一次设置就绪状态时启动，避免重复调用产生警告
            if (firstTimeReady)
            {
                StartHeartbeatLoop();
            }
        }
        
        /// <summary>
        /// 启动心跳循环
        /// </summary>
        private void StartHeartbeatLoop()
        {
            // ✅ 防止重复启动心跳循环
            if (_heartbeatLoopTask != null && !_heartbeatLoopTask.IsCompleted)
            {
                return;
            }
            
            _heartbeatLoopTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100, _cancellationToken);
                    
                    int heartbeatCount = 0;
                    int consecutiveFailures = 0;
                    
                    while (!_cancellationToken.IsCancellationRequested && _isReady && !_isStopping)
                    {
                        if (_isStopping || _cipher == null || _udpClient == null || _remoteEndPoint == null)
                        {
                            if (_isStopping)
                            {
                                break;
                            }
                            await Task.Delay(HEARTBEAT_INTERVAL_MS, _cancellationToken);
                            continue;
                        }
                        
                        try
                        {
                            var heartbeat = ProtoCodec.BuildHeartbeat();
                            SendData(heartbeat, channel: 1, flag: 1, proto: true);
                            
                            consecutiveFailures = 0;
                            heartbeatCount++;
                            
                            // 记录心跳发送（首次和每10次）
                            if (heartbeatCount == 1 || heartbeatCount % HEARTBEAT_LOG_INTERVAL == 0)
                            {
                                _logger.LogDebug("Heartbeat sent: count={Count}", heartbeatCount);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果正在停止，直接退出
                            if (_isStopping)
                            {
                                break;
                            }
                            consecutiveFailures++;
                            if (consecutiveFailures >= 3)
                            {
                                _logger.LogError(ex, "Heartbeat failed {Count} times consecutively", consecutiveFailures);
                                
                                // ✅ 关键修复：如果心跳连续失败超过 10 次，尝试恢复
                                // 这可能是 UDP 客户端或网络出现了问题
                                if (consecutiveFailures >= 10)
                                {
                                    _logger.LogWarning("⚠️ Heartbeat failed {Count} times consecutively, attempting recovery...", consecutiveFailures);
                                    
                                    // ✅ 检查 UDP 客户端状态
                                    if (_udpClient == null || _remoteEndPoint == null)
                                    {
                                        _logger.LogError("❌ UDP client or remote endpoint is null, cannot recover heartbeat");
                                        break; // 无法恢复，退出循环
                                    }
                                    
                                    // ✅ 重置连续失败计数，继续尝试（可能是临时网络问题）
                                    consecutiveFailures = 0;
                                    
                                    // ✅ 等待更长时间后重试
                                    await Task.Delay(2000, _cancellationToken);
                                }
                            }
                        }
                        
                        // 检查是否正在停止
                        if (_isStopping)
                        {
                            break;
                        }
                        
                        await Task.Delay(HEARTBEAT_INTERVAL_MS, _cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消，无需记录
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Heartbeat loop error");
                }
            }, _cancellationToken);
        }

        #endregion

        #region Send Methods

        /// <summary>
        /// 发送数据包
        /// </summary>
        private void SendData(byte[] data, int flag, int channel, bool proto = false)
        {
            int advanceBy = 0;
            if (_cipher != null)
            {
                AdvanceSequence();
                if (proto)
                {
                    advanceBy = data.Length;
                }
            }

            var packet = Packet.CreateData(_tsn, (ushort)channel, flag, data);
            SendPacket(packet, advanceBy);
        }

        /// <summary>
        /// 发送 DATA_ACK
        /// </summary>
        private void SendDataAck(uint ackTsn)
        {
            var packet = Packet.CreateDataAck(ackTsn);
            SendPacket(packet, advanceBy: PacketConstants.DATA_ACK_LENGTH);
        }

        /// <summary>
        /// 发送包
        /// </summary>
        private void SendPacket(byte[] packet, int? advanceBy = null)
        {
            // 如果正在停止，直接返回，不记录警告
            if (_isStopping)
            {
                return;
            }
            
            if (_udpClient == null || _remoteEndPoint == null)
            {
                // 只有在非停止状态下才记录警告
                if (!_isStopping)
                {
                    _logger.LogWarning("Cannot send packet: UDP client or remote endpoint is null");
                }
                return;
            }

            lock (_sendLock)
            {
                try
                {
                    // 如果有 cipher，需要计算 GMAC 和 key_pos
                    if (_cipher != null)
                    {
                        var keyPos = (uint)_cipher.KeyPos;
                        var tmp = new byte[packet.Length];
                        Buffer.BlockCopy(packet, 0, tmp, 0, packet.Length);

                        // 写入 tag_remote 和 key_pos
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(1, 4), _tagRemote);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(9, 4), keyPos);

                        // 清零 GMAC 和 key_pos 用于计算
                        if (tmp.Length >= 13)
                        {
                            Array.Clear(tmp, 5, 4);  // GMAC
                            Array.Clear(tmp, 9, 4);  // key_pos
                        }

                        // 计算 GMAC
                        var gmac = _cipher.GetGmacAtKeyPos(tmp, (int)keyPos);
                        var gmacValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(gmac);

                        // 写入 GMAC 和 key_pos
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), _tagRemote);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5, 4), gmacValue);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(9, 4), keyPos);

                        // 推进 key_pos
                        var advance = advanceBy ?? (packet.Length - PacketConst.HeaderLength - 4);
                        if (advance > 0)
                        {
                            _cipher.AdvanceKeyPos(advance);
                        }
                    }
                    else if (_tagRemote != 0)
                    {
                        // 没有 cipher 但有 tag_remote，只写入 tag_remote
                        // 注意：此时 GMAC 和 key_pos 应该保持为 0
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), _tagRemote);
                    }
                    else
                    {
                        _logger.LogWarning("Sending packet without tag_remote: tsn={Tsn}", _tsn);
                    }

                    _udpClient.Send(packet, packet.Length, _remoteEndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send packet");
                }
            }
        }

        /// <summary>
        /// 发送拥塞控制包（类型 0x05，15 字节）
        /// 根据 chiaki 的实现，需要先推进 key_pos，然后计算 GMAC
        /// </summary>
        private void SendCongestionControlPacket(byte[] packet)
        {
            if (_udpClient == null || _remoteEndPoint == null || _cipher == null)
            {
                if (!_isStopping)
                {
                    _logger.LogWarning("Cannot send congestion control packet: UDP client, remote endpoint or cipher is null");
                }
                return;
            }

            lock (_sendLock)
            {
                try
                {
                    // 拥塞控制包大小固定为 15 字节
                    const int CONGESTION_PACKET_SIZE = 15;
                    if (packet.Length != CONGESTION_PACKET_SIZE)
                    {
                        _logger.LogWarning("Invalid congestion control packet size: {Size}, expected {Expected}", 
                            packet.Length, CONGESTION_PACKET_SIZE);
                        return;
                    }

                    // 1. 先推进 key_pos（15 字节），获取新的 key_pos
                    // 根据 chiaki: chiaki_takion_crypt_advance_key_pos(takion, CHIAKI_TAKION_CONGESTION_PACKET_SIZE, &key_pos)
                    _cipher.AdvanceKeyPos(CONGESTION_PACKET_SIZE);
                    var keyPos = (uint)_cipher.KeyPos;

                    // 2. 更新包中的 key_pos（偏移 0x0b-0x0e）
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        packet.AsSpan(0x0b, 4), keyPos);

                    // 3. 创建临时副本用于计算 GMAC
                    var tmp = new byte[packet.Length];
                    Buffer.BlockCopy(packet, 0, tmp, 0, packet.Length);

                    // 4. 清零 GMAC（偏移 0x07-0x0a）和 key_pos（偏移 0x0b-0x0e）用于计算
                    // 根据 chiaki: 在计算 GMAC 时需要临时清零 key_pos
                    Array.Clear(tmp, 0x07, 4);  // GMAC
                    Array.Clear(tmp, 0x0b, 4);  // key_pos

                    // 5. 计算 GMAC（使用新的 key_pos）
                    // 根据 chiaki: chiaki_takion_packet_mac 会处理拥塞控制包的特殊逻辑
                    var gmac = _cipher.GetGmacAtKeyPos(tmp, (int)keyPos);
                    var gmacValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(gmac);

                    // 6. 写入 GMAC（偏移 0x07-0x0a）
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        packet.AsSpan(0x07, 4), gmacValue);

                    // 7. 发送包
                    _udpClient.Send(packet, packet.Length, _remoteEndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send congestion control packet");
                }
            }
        }

        /// <summary>
        /// 发送原始数据
        /// </summary>
        private void SendRaw(byte[] data)
        {
            // SendPacket 内部已经检查 _isStopping，这里直接调用即可
            SendPacket(data);
        }

        /// <summary>
        /// 推进序列号
        /// </summary>
        private void AdvanceSequence()
        {
            if (_state == STATE_INIT)
                return;
            _tsn++;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// 检查是否为重复的 TSN
        /// </summary>
        private bool IsDuplicateTsn(uint tsn)
        {
            return _processedTsns.Contains(tsn);
        }

        /// <summary>
        /// 标记 TSN 为已处理
        /// </summary>
        private void MarkTsnAsProcessed(uint tsn)
        {
            if (_processedTsns.Add(tsn))
            {
                _processedTsnsQueue.Enqueue(tsn);
                while (_processedTsnsQueue.Count > MAX_PROCESSED_TSNS)
                {
                    var oldTsn = _processedTsnsQueue.Dequeue();
                    _processedTsns.Remove(oldTsn);
                }
            }
        }

        /// <summary>
        /// 等待 ACK
        /// </summary>
        public void WaitForAck(uint tsn, Action callback)
        {
            _ackCallback = callback;
            _ackCallbackTsn = tsn;
        }

        #endregion

        #region Properties

        public string State => _state ?? STATE_INIT;
        public uint Tsn => _tsn;
        public bool IsReady => _isReady;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            try
            {
                // 避免在 Dispose 中同步等待异步导致死锁/卡死
                _ = Task.Run(() => StopAsync());
            }
            catch
            {
                // ignore
            }
            finally
            {
                _avHandler?.Stop();
            }
        }

        #endregion
    }

    /// <summary>
    /// DATA_ACK 长度常量
    /// </summary>
    internal static class PacketConstants
    {
        public const int DATA_ACK_LENGTH = 29;
    }
}

