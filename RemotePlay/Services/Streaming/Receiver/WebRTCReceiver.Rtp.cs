using SIPSorcery.Media;
using SIPSorcery.Net;
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Concentus;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver RTP 发送部分
    /// </summary>
    public sealed partial class WebRTCReceiver
    {
        private readonly record struct AudioRtpPacket(byte[] Payload, uint Timestamp, ushort SeqNum, int PayloadType);

        private const int AUDIO_SEND_QUEUE_CAPACITY = 10;
        private Channel<AudioRtpPacket>? _audioSendChannel;
        private CancellationTokenSource? _audioSendCts;
        private Task? _audioSendTask;
        private int _audioSendFailureCount;

        private void StartAudioSendLoop()
        {
            _audioSendChannel = Channel.CreateBounded<AudioRtpPacket>(new BoundedChannelOptions(AUDIO_SEND_QUEUE_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _audioSendCts = new CancellationTokenSource();
            _audioSendTask = Task.Run(() => AudioSendLoopAsync(_audioSendChannel.Reader, _audioSendCts.Token));
        }

        private void StopAudioSendLoop()
        {
            try
            {
                _audioSendCts?.Cancel();
                _audioSendChannel?.Writer.TryComplete();
                _audioSendTask?.Wait(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception while stopping audio send loop");
            }
            finally
            {
                _audioSendCts?.Dispose();
                _audioSendCts = null;
            }
        }

        private async Task AudioSendLoopAsync(ChannelReader<AudioRtpPacket> reader, CancellationToken token)
        {
            try
            {
                await foreach (var packet in reader.ReadAllAsync(token))
                {
                    var peerConnection = _peerConnection;
                    if (peerConnection == null ||
                        peerConnection.connectionState is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                    {
                        continue;
                    }

                    try
                    {
                        peerConnection.SendRtpRaw(SDPMediaTypesEnum.audio, packet.Payload, packet.Timestamp, 0, packet.PayloadType, packet.SeqNum);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.Increment(ref _audioSendFailureCount) % 100 == 1)
                        {
                            _logger.LogWarning(ex, "⚠️ Audio RTP send failed ({Count} failures total)", _audioSendFailureCount);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Audio send loop exited unexpectedly");
            }
        }

        /// <summary>
        /// 入队一个音频 RTP 包，由单一发送线程按序发出（序号和时间戳取自已构建的 RTP 头）
        /// </summary>
        private void SendAudioRTPRaw(byte[] rtpBytes, byte[] originalData, int payloadType = 111)
        {
            if (_peerConnection == null || rtpBytes.Length < 12 || _audioSendChannel == null)
            {
                return;
            }

            ushort seqNum = BinaryPrimitives.ReadUInt16BigEndian(rtpBytes.AsSpan(2, 2));
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(rtpBytes.AsSpan(4, 4));
            _audioSendChannel.Writer.TryWrite(new AudioRtpPacket(originalData, timestamp, seqNum, payloadType));
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
                _logger.LogError(ex, "❌ Failed to send PCM to WebRTC");
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
                _logger.LogError(ex, "❌ Failed to send PCM RTP packet");
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
                            _logger.LogInformation("✅ Opus decoder initialized: {SampleRate}Hz, {Channels} channels (via OpusCodecFactory)",
                                _audioSampleRate, _audioChannels);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Failed to initialize Opus decoder");
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
                            _logger.LogWarning("⚠️ Opus decode returned 0 samples");
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
                _logger.LogError(ex, "❌ Failed to send audio PCM");
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
                        _logger.LogDebug("⏳ Waiting for audio channel to be ready: signaling={Signaling}, connection={Connection}, ICE={Ice}", 
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
                _logger.LogError(ex, "❌ Failed to send PCMA RTP packet");
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
                _logger.LogError(ex, "❌ Failed to send PCMU RTP packet");
            }
        }
    }
}

