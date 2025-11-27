using SIPSorcery.Net;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed partial class WebRTCReceiver
    {
        /// <summary>
        /// 安全调用反射方法，带超时保护（防止 WebRTC 发送阻塞）
        /// ✅ 修复：返回是否成功，避免超时或失败时静默丢弃视频包
        /// </summary>
        private bool SafeInvokeMethod(Action invokeAction, string methodName, int timeoutMs = 100)
        {
            var invokeTask = Task.Run(invokeAction);
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = Task.WhenAny(invokeTask, timeoutTask).GetAwaiter().GetResult();
            
            if (completedTask == timeoutTask)
            {
                if (_videoPacketCount % 10 == 0) // 限流日志
                {
                    _logger.LogWarning("⚠️ {Method} 调用超时（{Timeout}ms），可能 WebRTC 发送阻塞，视频包可能丢失", methodName, timeoutMs);
                }
                return false; // 超时后返回 false，表示发送失败
            }
            
            // 检查是否有异常
            if (invokeTask.IsFaulted)
            {
                var ex = invokeTask.Exception?.InnerException ?? invokeTask.Exception ?? new Exception($"{methodName} failed");
                if (_videoPacketCount % 10 == 0)
                {
                    _logger.LogWarning(ex, "⚠️ {Method} 调用失败，视频包可能丢失", methodName);
                }
                throw ex;
            }
            
            return true; // 成功
        }
        
        /// <summary>
        /// ✅ 优先发送IDR关键帧（用于关键帧优先处理）
        /// </summary>
        public void OnVideoPacketPriority(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    return;
                }

                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);

                if (_peerConnection == null)
                {
                    return;
                }

                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);

                // ✅ B. 当IDR到来时立刻强制丢弃所有待处理的非IDR帧（避免过期帧导致跳帧/抖动）
                lock (_videoQueueLock)
                {
                    int droppedCount = _videoFrameQueue.Count;
                    _videoFrameQueue.Clear();
                    if (droppedCount > 0)
                    {
                        _logger.LogInformation("🗑️ IDR帧到达：丢弃 {Dropped} 个待处理的非IDR帧，避免过期帧导致跳帧", droppedCount);
                    }
                    
                    // 将IDR帧加入优先队列
                    _videoIdrQueue.Enqueue(videoData);
                }
                
                // ✅ 修复Bug 1: 在锁外调用ProcessVideoQueue，避免死锁
                ProcessVideoQueue();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 优先发送视频包失败");
            }
        }
        
        public void OnVideoPacket(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    if (_videoPacketCount < 3 && packet != null && packet.Length == 1)
                    {
                        _logger.LogError("❌ 视频包异常：长度只有 1 字节");
                    }
                    return;
                }

                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);

                if (_peerConnection == null)
                {
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ OnVideoPacket: _peerConnection is null, 已收到 {Count} 个视频包", _videoPacketCount);
                    }
                    return;
                }

                var (connectionState, iceState, signalingState) = GetCachedConnectionState();
                
                // ✅ 添加详细的连接状态诊断日志
                if (connectionState != RTCPeerConnectionState.connected &&
                    connectionState != RTCPeerConnectionState.connecting)
                {
                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ WebRTC 连接状态异常: connection={State}, ICE={IceState}, signaling={Signaling}, 已收到 {Count} 个视频包",
                            connectionState, iceState, signalingState, _videoPacketCount);
                    }
                }

                var videoData = new byte[packet.Length - 1];
                packet.AsSpan(1).CopyTo(videoData);

                // ✅ 普通帧进入队列，由ProcessVideoQueue处理
                lock (_videoQueueLock)
                {
                    int totalQueueSize = _videoFrameQueue.Count + _videoIdrQueue.Count;
                    if (totalQueueSize >= MAX_VIDEO_QUEUE_SIZE)
                    {
                        // 队列已满，丢弃最旧的普通帧
                        if (_videoFrameQueue.Count > 0)
                        {
                            _videoFrameQueue.Dequeue();
                            if (_videoPacketCount % 100 == 0)
                            {
                                _logger.LogWarning("⚠️ 视频队列已满，丢弃旧帧（队列大小: {Size}）", totalQueueSize);
                            }
                        }
                    }
                    _videoFrameQueue.Enqueue(videoData);
                }
                
                // ✅ 修复Bug 1: 在锁外调用ProcessVideoQueue，避免死锁
                // ✅ 修复Bug 2: 移除无条件return，保留队列处理逻辑
                ProcessVideoQueue();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送视频包失败: packetLen={Len}, count={Count}",
                    packet?.Length ?? 0, _videoPacketCount);
            }
        }

        private bool TrySendVideoDirect(byte[] videoData)
        {
            if (_peerConnection == null || _videoTrack == null || videoData == null || videoData.Length == 0)
                return false;

            try
            {
                var (connectionState, iceState, signalingState) = GetCachedConnectionState();

                bool canSendVideo = signalingState == RTCSignalingState.stable ||
                                    (signalingState == RTCSignalingState.have_local_offer &&
                                     (iceState == RTCIceConnectionState.connected ||
                                      iceState == RTCIceConnectionState.checking ||
                                      connectionState == RTCPeerConnectionState.connected ||
                                      connectionState == RTCPeerConnectionState.connecting));

                if (!canSendVideo)
                {
                    return false;
                }

                if (!_methodsInitialized)
                {
                    InitializeReflectionMethods();
                }

                if (_cachedSendVideoMethod != null)
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (_videoPacketCount > 0)
                        {
                            var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                            _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                        }
                        _lastVideoPacketTime = now;

                        // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                        bool sent = SafeInvokeMethod(
                            () => _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { _videoTimestamp, videoData }),
                            "SendVideo",
                            100);

                        if (sent)
                        {
                            _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                            return true;
                        }
                        // ✅ 修复Bug 3: 如果发送失败，返回 false 继续尝试其他方式
                        return false;
                    }
                    catch (Exception ex)
                    {
                        if (_videoPacketCount < 3)
                        {
                            var innerEx = ex.InnerException ?? ex;
                            _logger.LogWarning("⚠️ SendVideo 直接发送失败: {Ex}", innerEx.Message);
                        }
                        _cachedSendVideoMethod = null;
                        _methodsInitialized = false;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                if (_videoPacketCount < 3)
                {
                    _logger.LogWarning("⚠️ TrySendVideoDirect 异常: {Ex}", ex.Message);
                }
                return false;
            }
        }

        private void SendVideoRTP(byte[] data)
        {
            try
            {
                if (_peerConnection == null || _videoTrack == null)
                {
                    return;
                }

                var (connectionState, iceState, signalingState) = GetCachedConnectionState();

                // ✅ 修复：放宽状态检查条件，允许在更多状态下尝试发送
                // 避免因为状态检查过严导致视频包被静默丢弃
                bool canSend = false;

                if (signalingState == RTCSignalingState.stable)
                {
                    if (connectionState == RTCPeerConnectionState.connected ||
                        connectionState == RTCPeerConnectionState.connecting)
                    {
                        canSend = true;
                    }
                    else if (iceState == RTCIceConnectionState.connected ||
                             iceState == RTCIceConnectionState.checking)
                    {
                        canSend = true;
                    }
                }
                // ✅ 修复：即使信令状态不是 stable，如果 ICE 已连接，也尝试发送
                // 这可以处理 Answer 设置后但信令状态还未更新的情况
                else if (signalingState == RTCSignalingState.have_local_offer ||
                         signalingState == RTCSignalingState.have_remote_pranswer)
                {
                    if (iceState == RTCIceConnectionState.connected ||
                        iceState == RTCIceConnectionState.checking ||
                        connectionState == RTCPeerConnectionState.connected ||
                        connectionState == RTCPeerConnectionState.connecting)
                    {
                        canSend = true;
                    }
                }

                if (!canSend)
                {
                    // ✅ 关键修复：当连接状态异常时，更频繁地记录日志，帮助诊断问题
                    if (_videoPacketCount < 10 || _videoPacketCount % 50 == 0)
                    {
                        _logger.LogWarning("⚠️ WebRTC 状态不允许发送: connection={State}, ICE={IceState}, signaling={Signaling}, 已收到 {Count} 个包",
                            connectionState, iceState, signalingState, _videoPacketCount);
                        if (signalingState != RTCSignalingState.stable)
                        {
                            _logger.LogWarning("⚠️ SDP 协商未完成（{SignalingState}），需要等待 Answer 并设置为 stable", signalingState);
                        }
                        if (connectionState == RTCPeerConnectionState.@new)
                        {
                            _logger.LogWarning("⚠️ 连接状态还是 new，等待连接建立...");
                        }
                        if (connectionState == RTCPeerConnectionState.closed || connectionState == RTCPeerConnectionState.disconnected)
                        {
                            _logger.LogError("❌ WebRTC 连接已断开或关闭！需要重新建立连接");
                        }
                    }
                    return;
                }

                bool hasStartCode = (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 &&
                                   (data[2] == 0x00 && data[3] == 0x01 || data[2] == 0x01));

                if (hasStartCode && data.Length < 50000)
                {
                    try
                    {
                        if (_cachedSendVideoMethod != null)
                        {
                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                            // ✅ 修复：检查返回值，如果失败则继续尝试其他发送方式
                            bool sent = SafeInvokeMethod(
                                () => _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { _videoTimestamp, data }),
                                "SendVideo",
                                100);
                            
                            if (sent)
                            {
                                return;
                            }
                            // 如果超时或失败，继续尝试 NAL 解析方式
                        }
                    }
                    catch (Exception ex)
                    {
                        // ✅ 修复：记录异常但不吞掉，继续尝试其他发送方式
                        if (_videoPacketCount % 10 == 0)
                        {
                            _logger.LogWarning(ex, "⚠️ SendVideo 直接发送失败，尝试 NAL 解析方式");
                        }
                    }
                }

                var nalUnits = ParseAnnexBNalUnits(data);

                // ✅ 修复：如果 NAL 解析失败，尝试直接发送原始数据（可能是单个 NAL unit）
                if (nalUnits.Count == 0)
                {
                    if (_videoPacketCount < 5 || _videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 未解析到 NAL units，尝试直接发送原始数据。数据长度: {Length}, 前 16 字节: {Hex}",
                            data.Length,
                            data.Length > 0 ? Convert.ToHexString(data.Take(Math.Min(16, data.Length)).ToArray()) : "empty");
                    }
                    
                    // ✅ 修复：如果解析失败，尝试直接发送原始数据（可能是单个 NAL unit 或已封装格式）
                    // 这可以处理某些特殊情况下的视频数据格式
                    try
                    {
                        if (_cachedSendVideoMethod != null)
                        {
                            bool sent = SafeInvokeMethod(
                                () => _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { _videoTimestamp, data }),
                                "SendVideo(raw)",
                                100);
                            if (sent)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_videoPacketCount < 5 || _videoPacketCount % 100 == 0)
                        {
                            _logger.LogWarning(ex, "⚠️ 直接发送原始视频数据失败");
                        }
                    }
                    
                    // 如果直接发送也失败，返回（不继续处理空列表）
                    return;
                }

                for (int i = 0; i < nalUnits.Count; i++)
                {
                    var nalUnit = nalUnits[i];
                    if (nalUnit.Length == 0) continue;

                    bool isVideoFrame = false;

                    if (_detectedVideoFormat == "hevc")
                    {
                        byte nalType = (byte)((nalUnit[0] >> 1) & 0x3F);
                        if (nalType >= 1 && nalType <= 21)
                        {
                            isVideoFrame = true;
                        }
                    }
                    else
                    {
                        byte nalType = (byte)(nalUnit[0] & 0x1F);
                        if (nalType >= 1 && nalType <= 5)
                        {
                            isVideoFrame = true;
                        }
                    }

                    if (nalUnit.Length > RTP_MTU - 12)
                    {
                        SendFragmentedNalUnit(nalUnit);
                    }
                    else
                    {
                        // 最后一个 NAL 作为帧结束，设置 Marker
                        bool isLastNal = (i == nalUnits.Count - 1);
                        SendSingleNalUnit(nalUnit, isLastNal);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送视频 RTP 包失败");
            }
        }

        private List<byte[]> ParseAnnexBNalUnits(byte[] data)
        {
            var nalUnits = new List<byte[]>();
            if (data == null || data.Length < 4) return nalUnits;

            Span<byte> dataSpan = data;
            int currentPos = 0;

            while (currentPos < dataSpan.Length - 3)
            {
                int startCodePos = -1;
                int startCodeLength = 0;

                for (int i = currentPos; i < dataSpan.Length - 3; i++)
                {
                    if (dataSpan[i] == 0x00 && dataSpan[i + 1] == 0x00)
                    {
                        if (i + 3 < dataSpan.Length && dataSpan[i + 2] == 0x00 && dataSpan[i + 3] == 0x01)
                        {
                            startCodePos = i;
                            startCodeLength = 4;
                            break;
                        }
                        else if (i + 2 < dataSpan.Length && dataSpan[i + 2] == 0x01)
                        {
                            startCodePos = i;
                            startCodeLength = 3;
                            break;
                        }
                    }
                }

                if (startCodePos == -1)
                {
                    break;
                }

                int nextStartCodePos = -1;
                int nextStartCodeLength = 0;
                int searchStart = startCodePos + startCodeLength;

                for (int i = searchStart; i < dataSpan.Length - 3; i++)
                {
                    if (dataSpan[i] == 0x00 && dataSpan[i + 1] == 0x00)
                    {
                        if (i + 3 < dataSpan.Length && dataSpan[i + 2] == 0x00 && dataSpan[i + 3] == 0x01)
                        {
                            nextStartCodePos = i;
                            nextStartCodeLength = 4;
                            break;
                        }
                        else if (i + 2 < dataSpan.Length && dataSpan[i + 2] == 0x01)
                        {
                            nextStartCodePos = i;
                            nextStartCodeLength = 3;
                            break;
                        }
                    }
                }

                int nalStart = startCodePos + startCodeLength;
                int nalEnd = nextStartCodePos == -1 ? dataSpan.Length : nextStartCodePos;
                int nalLength = nalEnd - nalStart;

                if (nalLength > 0)
                {
                    var nalUnit = dataSpan.Slice(nalStart, nalLength).ToArray();
                    nalUnits.Add(nalUnit);
                }

                if (nextStartCodePos == -1)
                {
                    break;
                }
                currentPos = nextStartCodePos;
            }

            return nalUnits;
        }

        private void SendSingleNalUnit(byte[] nalUnit, bool isFrameEnd)
        {
            if (_peerConnection == null || _videoTrack == null || nalUnit.Length == 0) return;

            try
            {
                var rtpPacket = new RTPPacket(12 + nalUnit.Length);
                rtpPacket.Header.Version = 2;

                int payloadType = _detectedVideoFormat == "hevc" ? _negotiatedPtHevc : _negotiatedPtH264;

                rtpPacket.Header.PayloadType = (byte)payloadType;
                rtpPacket.Header.SequenceNumber = _videoSequenceNumber;
                _videoSequenceNumber++;

                rtpPacket.Header.Timestamp = _videoTimestamp;
                rtpPacket.Header.SyncSource = _videoSsrc;
                rtpPacket.Header.MarkerBit = isFrameEnd ? 1 : 0;

                System.Buffer.BlockCopy(nalUnit, 0, rtpPacket.Payload, 0, nalUnit.Length);

                try
                {
                    byte[] rtpBytes = rtpPacket.GetBytes();

                    try
                    {
                        var peerConnectionType = _peerConnection.GetType();

                        var sendVideoMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Where(m => m.Name == "SendVideo")
                            .ToList();

                        if (sendVideoMethods.Count == 0)
                        {
                            var baseType = peerConnectionType.BaseType;
                            if (baseType != null)
                            {
                                sendVideoMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Where(m => m.Name == "SendVideo")
                                    .ToList();
                            }
                        }

                        bool videoSent = false;
                        foreach (var method in sendVideoMethods)
                        {
                            try
                            {
                                var parameters = method.GetParameters();

                                if (parameters.Length == 2)
                                {
                                    if (parameters[0].ParameterType == typeof(uint) &&
                                        parameters[1].ParameterType == typeof(byte[]))
                                    {
                                        // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                        bool sent = SafeInvokeMethod(
                                            () => method.Invoke(_peerConnection, new object[] { _videoTimestamp, nalUnit }),
                                            "SendVideo(nalUnit)",
                                            100);
                                        if (sent)
                                        {
                                            videoSent = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                                {
                                    var innerEx = ex.InnerException ?? ex;
                                    _logger.LogWarning("⚠️ SendVideo 调用失败: {Ex}, 内部异常: {InnerEx}",
                                        ex.Message, innerEx.Message);
                                }
                            }
                        }

                        if (videoSent) return;

                        var sendRtpRawMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Where(m => m.Name == "SendRtpRaw")
                            .ToList();

                        if (sendRtpRawMethods.Count == 0)
                        {
                            var baseType = peerConnectionType.BaseType;
                            if (baseType != null)
                            {
                                sendRtpRawMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Where(m => m.Name == "SendRtpRaw")
                                    .ToList();
                            }
                        }

                        bool rtpSent = false;
                        if (sendRtpRawMethods.Any())
                        {
                            foreach (var method in sendRtpRawMethods)
                            {
                                try
                                {
                                    var parameters = method.GetParameters();

                                    // ✅ 优先使用 5 参数版本（由库管理 SSRC），兼容性更好
                                    if (parameters.Length == 5)
                                    {
                                        if (parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                            parameters[1].ParameterType == typeof(byte[]) &&
                                            parameters[2].ParameterType == typeof(uint) &&
                                            parameters[3].ParameterType == typeof(int) &&
                                            parameters[4].ParameterType == typeof(int))
                                        {
                                            int payloadTypeInt = (int)rtpPacket.Header.PayloadType;
                                            if (payloadTypeInt < 0 || payloadTypeInt > 127)
                                            {
                                                payloadTypeInt = _detectedVideoFormat == "hevc" ? _negotiatedPtHevc : _negotiatedPtH264;
                                            }

                                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                            bool sent = SafeInvokeMethod(
                                                () => method.Invoke(_peerConnection, new object[] {
                                                    SDPMediaTypesEnum.video,
                                                    rtpBytes,
                                                    rtpPacket.Header.Timestamp,
                                                    payloadTypeInt,
                                                    (int)rtpPacket.Header.SyncSource
                                                }),
                                                "SendRtpRaw(5)",
                                                100);
                                            if (sent)
                                            {
                                                rtpSent = true;
                                                break;
                                            }
                                        }
                                    }
                                    else if (parameters.Length == 6)
                                    {
                                        if (parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                            parameters[1].ParameterType == typeof(byte[]) &&
                                            parameters[2].ParameterType == typeof(uint) &&
                                            parameters[3].ParameterType == typeof(int) &&
                                            parameters[4].ParameterType == typeof(int) &&
                                            parameters[5].ParameterType == typeof(ushort))
                                        {
                                            ushort seqNum = _videoSequenceNumber;

                                            int payloadTypeInt = _detectedVideoFormat == "hevc" ? _negotiatedPtHevc : _negotiatedPtH264;
                                            if (rtpPacket.Header.PayloadType < 0 || rtpPacket.Header.PayloadType > 127)
                                            {
                                                _logger.LogWarning("⚠️ RTP Header PayloadType 超出范围: {PayloadType}, 使用计算值: {Computed}",
                                                    rtpPacket.Header.PayloadType, payloadTypeInt);
                                            }
                                            else
                                            {
                                                payloadTypeInt = (int)rtpPacket.Header.PayloadType;
                                            }

                                            // ✅ 避免手动指定 SSRC，使用 5 参数版本更稳；6 参数只作为后备
                                            int ssrcInt = (int)(_videoSsrc & 0x7FFFFFFF);

                                            try
                                            {
                                                // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                                bool sent = SafeInvokeMethod(
                                                    () => method.Invoke(_peerConnection, new object[] {
                                                        SDPMediaTypesEnum.video,
                                                        rtpBytes,
                                                        rtpPacket.Header.Timestamp,
                                                        payloadTypeInt,
                                                        ssrcInt,
                                                        seqNum
                                                    }),
                                                    "SendRtpRaw(6)",
                                                    100);
                                                if (sent)
                                                {
                                                    rtpSent = true;
                                                    break;
                                                }
                                            }
                                            catch (Exception invokeEx)
                                            {
                                                var innerEx = invokeEx.InnerException ?? invokeEx;
                                                _logger.LogError(innerEx, "❌ SendRtpRaw 调用异常: seq={Seq}, payloadType={Pt}, ssrc={Ssrc}, ts={Ts}, rtpBytesLen={Len}, 错误: {Error}",
                                                    seqNum, payloadTypeInt, ssrcInt, rtpPacket.Header.Timestamp, rtpBytes.Length, innerEx.Message);

                                                if (innerEx.Message.Contains("UInt16"))
                                                {
                                                    _logger.LogError("❌ UInt16 参数检查: seqNum={Seq} (range: 0-65535), rtpBytesLen={Len} (int, not UInt16)",
                                                        seqNum, rtpBytes.Length);
                                                    _logger.LogError("❌ 可能的问题: RTP header 中的序列号字段可能不正确");
                                                }
                                                throw;
                                            }
                                        }
                                    }
                                    else if (parameters.Length == 2)
                                    {
                                        if (parameters[0].ParameterType == typeof(byte[]) &&
                                            parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                                        {
                                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                            bool sent = SafeInvokeMethod(
                                                () => method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.video }),
                                                "SendRtpRaw(2)",
                                                100);
                                            if (sent)
                                            {
                                                rtpSent = true;
                                                break;
                                            }
                                        }
                                        else if (parameters[0].ParameterType == typeof(byte[]) &&
                                                 parameters[1].ParameterType == typeof(int))
                                        {
                                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                            bool sent = SafeInvokeMethod(
                                                () => method.Invoke(_peerConnection, new object[] { rtpBytes, payloadType }),
                                                "SendRtpRaw(2-int)",
                                                100);
                                            if (sent)
                                            {
                                                rtpSent = true;
                                                break;
                                            }
                                        }
                                    }
                                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                        bool sent = SafeInvokeMethod(
                                            () => method.Invoke(_peerConnection, new object[] { rtpBytes }),
                                            "SendRtpRaw(1)",
                                            100);
                                        if (sent)
                                        {
                                            rtpSent = true;
                                            break;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                                    {
                                        var innerEx = ex.InnerException ?? ex;
                                        _logger.LogWarning("⚠️ SendRtpRaw 调用失败: {Ex}, 内部异常: {InnerEx}, 方法参数: {Params}",
                                            ex.Message, innerEx.Message, string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)));
                                    }
                                }
                            }

                            if (rtpSent) return;
                        }
                        else
                        {
                            if (_videoPacketCount == 0)
                            {
                                _logger.LogWarning("⚠️ 未找到 SendRtpRaw 方法");
                            }
                        }

                        if (videoSent) return;

                        if (_videoPacketCount == 0 || _videoPacketCount % 100 == 0)
                        {
                            _logger.LogError("❌ 所有 SendVideo 方法调用都失败了！");
                            _logger.LogError("❌ 连接状态: {State}, ICE: {Ice}, 信令: {Signaling}",
                                _peerConnection.connectionState, _peerConnection.iceConnectionState, _peerConnection.signalingState);
                            _logger.LogError("❌ 视频轨道状态: {Track}", _videoTrack != null ? "存在" : "不存在");

                            var allMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                                           m.Name.Contains("Rtp", StringComparison.OrdinalIgnoreCase))
                                .Select(m =>
                                {
                                    var paramsStr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                                    return $"{m.Name}({paramsStr})";
                                })
                                .ToList();
                            if (allMethods.Any())
                            {
                                _logger.LogError("❌ 可用的发送方法: {Methods}", string.Join("; ", allMethods));
                            }
                        }

                        if (_videoTrack != null)
                        {
                            var trackType = _videoTrack.GetType();
                            var trackMethods = trackType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            foreach (var method in trackMethods)
                            {
                                try
                                {
                                    var parameters = method.GetParameters();
                                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                        bool sent = SafeInvokeMethod(
                                            () => method.Invoke(_videoTrack, new object[] { nalUnit }),
                                            "SendVideoTrack",
                                            100);
                                        if (sent)
                                        {
                                            return;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        if (_videoPacketCount == 0)
                        {
                            var allMethods = peerConnectionType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                                           m.Name.Contains("Rtp", StringComparison.OrdinalIgnoreCase))
                                .Select(m =>
                                {
                                    var paramsStr = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name}"));
                                    return $"{m.Name}({paramsStr})";
                                })
                                .ToList();
                            _logger.LogWarning("⚠️ 未找到可用的发送方法。所有相关方法: {Methods}", string.Join("; ", allMethods));
                        }
                        else if (_videoPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 未找到可用的 SendVideo 或 SendRtpRaw 方法");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_videoPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 发送 RTP 包异常: {Ex}", ex.Message);
                        }
                    }

                    if (_videoPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ RTP 包已构建但未发送（需要找到正确的发送 API）: seq={Seq}, size={Size}",
                            rtpPacket.Header.SequenceNumber, rtpBytes.Length);
                    }
                }
                catch (Exception sendEx)
                {
                    _logger.LogError(sendEx, "❌ 发送 RTP 包失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送单个 NAL unit RTP 包失败");
            }
        }

        private void SendFragmentedNalUnit(byte[] nalUnit)
        {
            if (_peerConnection == null || _videoTrack == null || nalUnit.Length == 0) return;

            byte nalType = (byte)(nalUnit[0] & 0x1F);
            byte nalHeader = (byte)(nalUnit[0] & 0x60);

            int maxFragmentSize = RTP_MTU - 12 - 2;
            int fragmentCount = (nalUnit.Length + maxFragmentSize - 1) / maxFragmentSize;

            for (int i = 0; i < fragmentCount; i++)
            {
                int fragmentStart = i * maxFragmentSize;
                int fragmentLength = Math.Min(maxFragmentSize, nalUnit.Length - fragmentStart);

                try
                {
                    var rtpPacket = new RTPPacket(12 + 2 + fragmentLength);
                    rtpPacket.Header.Version = 2;
                    rtpPacket.Header.PayloadType = (byte)(_detectedVideoFormat == "hevc" ? 97 : 96);

                    rtpPacket.Header.SequenceNumber = _videoSequenceNumber;
                    _videoSequenceNumber++;

                    rtpPacket.Header.Timestamp = _videoTimestamp;
                    rtpPacket.Header.SyncSource = _videoSsrc;

                    byte fuIndicator = (byte)(nalHeader | 28);
                    byte fuHeader = (byte)(nalType);

                    if (i == 0)
                    {
                        fuHeader |= 0x80;
                        rtpPacket.Header.MarkerBit = 0;
                    }
                    else if (i == fragmentCount - 1)
                    {
                        fuHeader |= 0x40;
                        rtpPacket.Header.MarkerBit = 1; // 最后一片标记帧结束
                    }
                    else
                    {
                        rtpPacket.Header.MarkerBit = 0;
                    }

                    rtpPacket.Payload[0] = fuIndicator;
                    rtpPacket.Payload[1] = fuHeader;
                    System.Buffer.BlockCopy(nalUnit, fragmentStart, rtpPacket.Payload, 2, fragmentLength);

                    try
                    {
                        // ✅ 优先使用 5 参数 SendRtpRaw（由库管理 SSRC），兼容性更好
                        var sendRtpRawMethods = _peerConnection.GetType().GetMethods()
                            .Where(m => m.Name == "SendRtpRaw")
                            .ToList();

                        bool sent = false;
                        foreach (var method in sendRtpRawMethods)
                        {
                            try
                            {
                                var parameters = method.GetParameters();
                                if (parameters.Length == 5 &&
                                    parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                    parameters[1].ParameterType == typeof(byte[]) &&
                                    parameters[2].ParameterType == typeof(uint) &&
                                    parameters[3].ParameterType == typeof(int) &&
                                    parameters[4].ParameterType == typeof(int))
                                {
                                    int payloadTypeInt = rtpPacket.Header.PayloadType;
                                    if (payloadTypeInt < 0 || payloadTypeInt > 127)
                                    {
                                        payloadTypeInt = (_detectedVideoFormat == "hevc") ? 97 : 96;
                                    }
                                    // 传入纯负载，由库封包
                                    var payloadOnly = new byte[2 + fragmentLength];
                                    payloadOnly[0] = fuIndicator;
                                    payloadOnly[1] = fuHeader;
                                    System.Buffer.BlockCopy(nalUnit, fragmentStart, payloadOnly, 2, fragmentLength);

                                    int markerBit = rtpPacket.Header.MarkerBit;
                                    // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                    bool fragmentSent = SafeInvokeMethod(
                                        () => method.Invoke(_peerConnection, new object[] {
                                            SDPMediaTypesEnum.video,
                                            payloadOnly,
                                            rtpPacket.Header.Timestamp,
                                            markerBit,
                                            payloadTypeInt
                                        }),
                                        "SendRtpRaw(fragment)",
                                        100);
                                    if (fragmentSent)
                                    {
                                        sent = true;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!sent)
                        {
                            // 后备：发送完整 RTP 字节（2 参数版本）
                            var rtpBytes = rtpPacket.GetBytes();
                            var methods2 = _peerConnection.GetType().GetMethods()
                                .Where(m => m.Name == "SendRtpRaw" && m.GetParameters().Length == 2)
                                .ToList();
                            foreach (var method in methods2)
                            {
                                try
                                {
                                    var parameters = method.GetParameters();
                                    if (parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        if (parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                                        {
                                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                            bool fragmentSent = SafeInvokeMethod(
                                                () => method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.video }),
                                                "SendRtpRaw(2-fragment)",
                                                100);
                                            if (fragmentSent)
                                            {
                                                sent = true;
                                                break;
                                            }
                                        }
                                        else if (parameters[1].ParameterType == typeof(int))
                                        {
                                            // ✅ 关键修复：使用超时保护，防止 WebRTC 发送阻塞
                                            bool fragmentSent = SafeInvokeMethod(
                                                () => method.Invoke(_peerConnection, new object[] { rtpBytes, (int)rtpPacket.Header.PayloadType }),
                                                "SendRtpRaw(2-int-fragment)",
                                                100);
                                            if (fragmentSent)
                                            {
                                                sent = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        if (!sent)
                        {
                            _logger.LogWarning("⚠️ 分片视频 RTP 包已构建但未发送（未匹配到 SendRtpRaw 方法）");
                        }
                    }
                    catch (Exception sendEx)
                    {
                        _logger.LogError(sendEx, "❌ 发送分片 RTP 包失败");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 发送分片 NAL unit RTP 包失败: fragment {I}/{Count}", i + 1, fragmentCount);
                }
            }
        }

        private string? DetectCodecFromVideoHeader(byte[] header)
        {
            if (header == null || header.Length < 5)
            {
                return null;
            }

            int actualHeaderLen = header.Length >= 64 ? header.Length - 64 : header.Length;

            for (int i = 0; i < actualHeaderLen - 4; i++)
            {
                if (i + 4 < actualHeaderLen &&
                    header[i] == 0x00 && header[i + 1] == 0x00 &&
                    header[i + 2] == 0x00 && header[i + 3] == 0x01)
                {
                    byte nalType = header[i + 4];

                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }

                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }

                if (i + 3 < actualHeaderLen &&
                    header[i] == 0x00 && header[i + 1] == 0x00 && header[i + 2] == 0x01)
                {
                    byte nalType = header[i + 3];

                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                    {
                        return "hevc";
                    }

                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                    {
                        return "h264";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// ✅ 处理视频队列：优先发送IDR帧，然后发送普通帧
        /// </summary>
        private void ProcessVideoQueue()
        {
            lock (_videoQueueLock)
            {
                // ✅ 优先处理IDR队列
                while (_videoIdrQueue.Count > 0)
                {
                    var idrFrame = _videoIdrQueue.Dequeue();
                    
                    // ✅ 更新时间戳
                    var now = DateTime.UtcNow;
                    if (_videoPacketCount > 0)
                    {
                        var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                        if (elapsed > 0 && elapsed < 1.0)
                        {
                            _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                        }
                        else
                        {
                            _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                        }
                    }
                    _lastVideoPacketTime = now;
                    
                    if (TrySendVideoDirect(idrFrame))
                    {
                        _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                        _videoPacketCount++;
                    }
                    else
                    {
                        // 发送失败，尝试RTP方式
                        SendVideoRTP(idrFrame);
                        _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                        _videoPacketCount++;
                    }
                }
                
                // ✅ 然后处理普通帧队列（限制每次处理的帧数，避免阻塞）
                int processed = 0;
                const int MAX_FRAMES_PER_BATCH = 3; // 每批最多处理3帧
                while (_videoFrameQueue.Count > 0 && processed < MAX_FRAMES_PER_BATCH)
                {
                    var frame = _videoFrameQueue.Dequeue();
                    
                    // ✅ 更新时间戳
                    var now = DateTime.UtcNow;
                    if (_videoPacketCount > 0)
                    {
                        var elapsed = (now - _lastVideoPacketTime).TotalSeconds;
                        if (elapsed > 0 && elapsed < 1.0)
                        {
                            _videoTimestamp += (uint)(elapsed * VIDEO_CLOCK_RATE);
                        }
                        else
                        {
                            _videoTimestamp += (uint)VIDEO_TIMESTAMP_INCREMENT;
                        }
                    }
                    _lastVideoPacketTime = now;
                    
                    if (TrySendVideoDirect(frame))
                    {
                        _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                        _videoPacketCount++;
                        processed++;
                    }
                    else
                    {
                        // 发送失败，尝试RTP方式
                        SendVideoRTP(frame);
                        _latencyStats?.RecordPacketSent(_sessionId, "video", _currentVideoFrameIndex);
                        _videoPacketCount++;
                        processed++;
                    }
                }
            }
        }
        
        private bool IsIdrFrame(byte[] buf, int hintOffset)
        {
            if (buf == null || buf.Length < 6) return false;

            bool AnnexBScan(int start)
            {
                for (int i = start; i <= buf.Length - 4; i++)
                {
                    if (buf[i] == 0x00 && buf[i + 1] == 0x00)
                    {
                        int nalStart = -1;
                        if (i + 3 < buf.Length && buf[i + 2] == 0x00 && buf[i + 3] == 0x01) nalStart = i + 4;
                        else if (buf[i + 2] == 0x01) nalStart = i + 3;
                        if (nalStart >= 0 && nalStart < buf.Length)
                        {
                            byte h = buf[nalStart];

                            int hevcType = (h >> 1) & 0x3F;
                            if (hevcType == 19 || hevcType == 20 || hevcType == 21 ||
                                hevcType == 16 || hevcType == 17 || hevcType == 18)
                            {
                                return true;
                            }

                            int h264Type = h & 0x1F;
                            if (h264Type == 5)
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            return AnnexBScan(0) || (hintOffset > 0 && AnnexBScan(hintOffset));
        }
    }
}


