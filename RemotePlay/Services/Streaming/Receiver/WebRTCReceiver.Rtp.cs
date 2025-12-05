using SIPSorcery.Media;
using SIPSorcery.Net;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Concentus;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver RTP 发送部分
    /// </summary>
    public sealed partial class WebRTCReceiver
    {
        /// <summary>
        /// ✅ 修复：添加超时保护和限流机制，避免同步阻塞导致前端冻结
        /// 使用异步方式调用，带超时机制和信号量限流
        /// </summary>
        private void SendAudioRTPRaw(byte[] rtpBytes, byte[] originalData, int payloadType = 111)
        {
            try
            {
                if (_peerConnection == null) return;
                
                // ✅ 音频限流优化：使用带超时的等待，避免直接丢帧导致爆音
                // 音频对连续性要求极高，丢帧会导致爆音、杂音等问题
                _ = Task.Run(async () =>
                {
                    // ✅ 修复：等待最多 20ms（音频帧间隔），避免直接丢帧
                    // 20ms = 1 帧 @ 50fps（音频通常 48kHz，10ms 或 20ms 一帧）
                    bool acquired = await _audioSendSemaphore.WaitAsync(20);
                    
                    if (!acquired)
                    {
                        // 仍然无法获取信号量，说明积压严重，此时才丢帧
                        if (_audioPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ 音频发送严重积压（等待 20ms 后仍无法发送），丢弃此帧");
                        }
                        return;
                    }
                    
                    try
                    {
                        await SendAudioRTPRawAsync(rtpBytes, originalData, payloadType);
                    }
                    catch (Exception ex)
                    {
                        if (_audioPacketCount % 100 == 0)
                        {
                            _logger.LogWarning(ex, "⚠️ 异步发送音频 RTP 包失败");
                        }
                    }
                    finally
                    {
                        _audioSendSemaphore.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 RTP 包失败");
            }
        }
        
        /// <summary>
        /// 异步发送音频 RTP 包，带超时保护
        /// </summary>
        private async Task<bool> SendAudioRTPRawAsync(byte[] rtpBytes, byte[] originalData, int payloadType = 111)
        {
            try
            {
                if (_peerConnection == null) return false;
                
                var peerConnectionType = _peerConnection.GetType();
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
                foreach (var method in sendRtpRawMethods)
                {
                    try
                    {
                        var parameters = method.GetParameters();
                        
                        if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte[]))
                        {
                            if (parameters[1].ParameterType == typeof(SDPMediaTypesEnum))
                            {
                                // ✅ 使用超时保护，避免阻塞
                                using var cts = new CancellationTokenSource(100); // 100ms 超时
                                var invokeTask = Task.Run(() => 
                                    method.Invoke(_peerConnection, new object[] { rtpBytes, SDPMediaTypesEnum.audio }), cts.Token);
                                
                                try
                                {
                                    await invokeTask;
                                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                    {
                                        _logger.LogDebug("✅ 音频 RTP 包已发送 (2参数, SDPMediaTypesEnum): size={Size}", rtpBytes.Length);
                                    }
                                    rtpSent = true;
                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    if (_audioPacketCount % 100 == 0)
                                    {
                                        _logger.LogWarning("⚠️ 音频 RTP 发送超时 (2参数, SDPMediaTypesEnum)");
                                    }
                                    continue;
                                }
                            }
                            else if (parameters[1].ParameterType == typeof(int))
                            {
                                // ✅ 使用超时保护，避免阻塞
                                using var cts = new CancellationTokenSource(100); // 100ms 超时
                                var invokeTask = Task.Run(() => 
                                    method.Invoke(_peerConnection, new object[] { rtpBytes, payloadType }), cts.Token);
                                
                                try
                                {
                                    await invokeTask;
                                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                    {
                                        _logger.LogDebug("✅ 音频 RTP 包已发送 (2参数, int): payloadType={Pt}, size={Size}", payloadType, rtpBytes.Length);
                                    }
                                    rtpSent = true;
                                    break;
                                }
                                catch (OperationCanceledException)
                                {
                                    if (_audioPacketCount % 100 == 0)
                                    {
                                        _logger.LogWarning("⚠️ 音频 RTP 发送超时 (2参数, int)");
                                    }
                                    continue;
                                }
                            }
                        }
                        else if (parameters.Length == 6 &&
                                 parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                                 parameters[1].ParameterType == typeof(byte[]) &&
                                 parameters[2].ParameterType == typeof(uint) &&
                                 parameters[3].ParameterType == typeof(int) &&
                                 parameters[4].ParameterType == typeof(int) &&
                                 parameters[5].ParameterType == typeof(ushort))
                        {
                            byte[] payloadData = originalData;
                            
                            uint timestamp = 0;
                            ushort seqNum = 0;
                            if (rtpBytes.Length >= 12)
                            {
                                seqNum = (ushort)((rtpBytes[2] << 8) | rtpBytes[3]);
                                timestamp = (uint)((rtpBytes[4] << 24) | (rtpBytes[5] << 16) | (rtpBytes[6] << 8) | rtpBytes[7]);
                            }
                            else
                            {
                                seqNum = (ushort)((_audioSequenceNumber - 1) & 0xFFFF);
                                timestamp = _audioTimestamp;
                            }
                            
                            int markerBit = 0;
                            
                            // ✅ 使用超时保护，避免阻塞
                            using var cts = new CancellationTokenSource(100); // 100ms 超时
                            var invokeTask = Task.Run(() => 
                                method.Invoke(_peerConnection, new object[] { 
                                    SDPMediaTypesEnum.audio, 
                                    payloadData,
                                    timestamp, 
                                    markerBit,
                                    payloadType,
                                    seqNum 
                                }), cts.Token);
                            
                            try
                            {
                                await invokeTask;
                                if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                                {
                                    _logger.LogDebug("✅ 音频 RTP 包已发送 (6参数): seq={Seq}, ts={Ts}, payloadType={Pt}, size={Size}", 
                                        seqNum, timestamp, payloadType, payloadData.Length);
                                }
                                rtpSent = true;
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                if (_audioPacketCount % 100 == 0)
                                {
                                    _logger.LogWarning("⚠️ 音频 RTP 发送超时 (6参数): seq={Seq}, ts={Ts}", seqNum, timestamp);
                                }
                                continue;
                            }
                            catch (Exception invokeEx)
                            {
                                var innerEx = invokeEx.InnerException ?? invokeEx;
                                _logger.LogError(innerEx, "❌ SendRtpRaw (6参数) 调用异常: seqNum={Seq}, timestamp={Ts}, payloadType={Pt}, payloadLen={Len}, error={Error}", 
                                    seqNum, timestamp, payloadType, payloadData.Length, innerEx.Message);
                                continue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                        {
                            var innerEx = ex.InnerException ?? ex;
                            _logger.LogWarning("⚠️ SendRtpRaw 调用失败: {Ex}, 方法: {Method}", 
                                innerEx.Message, 
                                string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)));
                        }
                    }
                }
                
                if (!rtpSent)
                {
                    if (_audioPacketCount % 100 == 0)
                    {
                        _logger.LogWarning("⚠️ 音频 RTP 包已构建但未发送: seq={Seq}, size={Size}, 找到方法数: {Count}", 
                            _audioSequenceNumber, rtpBytes.Length, sendRtpRawMethods.Count);
                        
                        if (sendRtpRawMethods.Count == 0)
                        {
                            _logger.LogWarning("⚠️ 未找到 SendRtpRaw 方法，检查连接状态: {State}, ICE: {Ice}, 信令: {Signaling}", 
                                _peerConnection?.connectionState, 
                                _peerConnection?.iceConnectionState, 
                                _peerConnection?.signalingState);
                        }
                    }
                }
                
                return rtpSent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 RTP 包失败");
                return false;
            }
        }

        private void SendAudioPCMToWebRTC(byte[] pcmData, int samplesDecoded)
        {
            try
            {
                _audioTimestamp += (uint)samplesDecoded;
                SendAudioPCMAsRTP(pcmData, samplesDecoded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCM 到 WebRTC 失败");
            }
        }
        
        private void SendAudioPCMAsRTP(byte[] pcmData, int samplesDecoded)
        {
            try
            {
                var rtpPacket = new RTPPacket(12 + pcmData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 111;
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                System.Buffer.BlockCopy(pcmData, 0, rtpPacket.Payload, 0, pcmData.Length);
                
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmData, 111);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCM RTP 包失败");
            }
        }

        private void SendAudioPCM(byte[] opusFrame)
        {
            try
            {
                if (_peerConnection == null || _audioTrack == null || opusFrame == null || opusFrame.Length == 0)
                {
                    return;
                }

                if (_peerConnection.connectionState != RTCPeerConnectionState.connected)
                {
                    return;
                }

                byte[]? pcmData = null;
                int samplesDecoded = 0;

                lock (_opusDecoderLock)
                {
                    if (_opusDecoder == null)
                    {
                        try
                        {
                            _opusDecoder = OpusCodecFactory.CreateDecoder(_audioSampleRate, _audioChannels);
                            _logger.LogInformation("✅ Opus 解码器已初始化: {SampleRate}Hz, {Channels} 声道 (使用 OpusCodecFactory)",
                                _audioSampleRate, _audioChannels);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ 初始化 Opus 解码器失败");
                            SendAudioOpusDirect(opusFrame);
                            return;
                        }
                    }

                    float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);

                    if (samplesDecoded > 0)
                    {
                        short[] pcmBuffer = new short[samplesDecoded * _audioChannels];
                        for (int i = 0; i < samplesDecoded * _audioChannels; i++)
                        {
                            float clamped = Math.Max(-1.0f, Math.Min(1.0f, pcmBufferFloat[i]));
                            pcmBuffer[i] = (short)(clamped * 32767.0f);
                        }
                        pcmData = new byte[samplesDecoded * _audioChannels * 2];
                        System.Buffer.BlockCopy(pcmBuffer, 0, pcmData, 0, pcmData.Length);
                    }
                    else
                    {
                        if (_audioPacketCount < 5)
                        {
                            _logger.LogWarning("⚠️ Opus 解码返回 0 个样本");
                        }
                        return;
                    }
                }

                if (pcmData != null && pcmData.Length > 0)
                {
                    SendAudioPCMToWebRTC(pcmData, samplesDecoded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送音频 PCM 失败");
            }
        }

        private void SendAudioPCMAAsRTP(byte[] pcmaData, int samplesDecoded)
        {
            try
            {
                if (_peerConnection == null || pcmaData == null || pcmaData.Length == 0)
                {
                    return;
                }
                
                var connectionState = _peerConnection.connectionState;
                var iceState = _peerConnection.iceConnectionState;
                var signalingState = _peerConnection.signalingState;
                
                bool canSend = signalingState == RTCSignalingState.stable ||
                               (signalingState == RTCSignalingState.have_local_offer && 
                                (iceState == RTCIceConnectionState.connected || 
                                 iceState == RTCIceConnectionState.checking ||
                                 connectionState == RTCPeerConnectionState.connected ||
                                 connectionState == RTCPeerConnectionState.connecting));
                
                if (iceState == RTCIceConnectionState.@new && signalingState == RTCSignalingState.have_local_offer)
                {
                    canSend = true;
                }
                
                if (!canSend)
                {
                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                    {
                        _logger.LogDebug("⏳ 等待音频通道就绪: signaling={Signaling}, connection={Connection}, ICE={Ice}", 
                            signalingState, connectionState, iceState);
                    }
                    return;
                }
                
                if (samplesDecoded == 0)
                {
                    samplesDecoded = 160;
                }
                _audioTimestamp += (uint)samplesDecoded;
                
                var rtpPacket = new RTPPacket(12 + pcmaData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 8;
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                System.Buffer.BlockCopy(pcmaData, 0, rtpPacket.Payload, 0, pcmaData.Length);
                
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmaData, 8);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCMA RTP 包失败");
            }
        }
        
        private void SendAudioPCMUAsRTP(byte[] pcmuData, int samplesDecoded)
        {
            try
            {
                if (_peerConnection == null || pcmuData == null || pcmuData.Length == 0)
                {
                    return;
                }
                
                var connectionState = _peerConnection.connectionState;
                var iceState = _peerConnection.iceConnectionState;
                var signalingState = _peerConnection.signalingState;
                
                bool canSend = signalingState == RTCSignalingState.stable ||
                               (signalingState == RTCSignalingState.have_local_offer && 
                                (iceState == RTCIceConnectionState.connected || 
                                 iceState == RTCIceConnectionState.checking ||
                                 connectionState == RTCPeerConnectionState.connected ||
                                 connectionState == RTCPeerConnectionState.connecting));
                
                if (iceState == RTCIceConnectionState.@new && signalingState == RTCSignalingState.have_local_offer)
                {
                    canSend = true;
                }
                
                if (!canSend)
                {
                    return;
                }
                
                if (samplesDecoded == 0)
                {
                    samplesDecoded = 160;
                }
                _audioTimestamp += (uint)samplesDecoded;
                
                var rtpPacket = new RTPPacket(12 + pcmuData.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 0;
                rtpPacket.Header.SequenceNumber = _audioSequenceNumber++;
                rtpPacket.Header.Timestamp = _audioTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                System.Buffer.BlockCopy(pcmuData, 0, rtpPacket.Payload, 0, pcmuData.Length);
                
                byte[] rtpBytes = rtpPacket.GetBytes();
                SendAudioRTPRaw(rtpBytes, pcmuData, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 发送 PCMU RTP 包失败");
            }
        }
    }
}

