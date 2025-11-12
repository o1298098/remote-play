using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Utils.Crypto;
using System;
using System.Collections.Concurrent;
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

        // 状态
        private string? _state;
        private uint _tsn = 1;
        private uint _tagLocal = 1;
        private uint _tagRemote = 0;
        private bool _isReady = false;
        private bool _isStopping = false;

        // 加密
        private StreamECDH? _ecdh;
        private StreamCipher? _cipher;

        // AV 处理
        private AVHandler? _avHandler;

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

            // 初始化 AVHandler
            _avHandler = new AVHandler(
                _loggerFactory.CreateLogger<AVHandler>(),
                _session.HostType,
                null, // cipher 稍后设置
                null, // receiver 稍后设置
                _cancellationToken
            );
            
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
            _logger.LogInformation("Stopping RPStream");

            try
            {
                // ✅ 先停止心跳循环
                _isReady = false; // 停止心跳循环
                
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

            // 等待接收循环退出（最多等待 1 秒）
            if (_receiveLoopTask != null)
            {
                try
                {
                    await Task.WhenAny(_receiveLoopTask, Task.Delay(1000));
                }
                catch { }
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
            SendData(data, channel: 1, flag: 2, proto: true);
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
                    
                    var result = await _udpClient.ReceiveAsync(_cancellationToken);
                    if (result.Buffer != null && result.Buffer.Length > 0)
                    {
                        HandleReceivedData(result.Buffer);
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
                    _avHandler.AddPacket(data);
                }
                else
                {
                    _logger.LogWarning("Received AV packet but AVHandler or receiver is null");
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
                    _logger.LogWarning("DISCONNECT received from PS5, handling disconnect...");
                    _ = Task.Run(async () => await HandleDisconnectAsync());
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
                
                // ✅ 关键修复：发送 IDRREQUEST 请求 PS5 发送 IDR 关键帧
                // PS5 默认不发送 IDR 帧，必须由客户端主动请求
              //  _ = Task.Run(async () =>
              //  {
              //      await Task.Delay(500);  // 等待服务稳定
              //      _logger.LogInformation("🎬 开始请求 IDR 关键帧...");
              //      await StartIdrRequesterAsync();
              //  });
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
                
                await Task.CompletedTask;  // 保持异步方法签名
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send IDRREQUEST");
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
        /// </summary>
        private async Task SendRawPacketAsync(byte[] packet)
        {
            // 如果正在停止，直接返回
            if (_isStopping || _cipher == null)
            {
                await Task.CompletedTask;
                return;
            }
            
            SendRaw(packet);
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
            // TODO: 实现包统计（如果需要）
            return (0, 0);
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

            // 提取视频和音频头
            var rawVideoHeader = streamInfo.Resolution?.FirstOrDefault()?.VideoHeader?.ToByteArray() ?? Array.Empty<byte>();
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
            StopAsync().Wait(1000);
            _avHandler?.Stop();
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

