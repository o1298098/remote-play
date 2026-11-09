using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming.Protocol;
using SIPSorcery.Media;
using SIPSorcery.Net;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// WebRTCReceiver 音频处理部分
    /// </summary>
    public sealed partial class WebRTCReceiver
    {
        public void OnAudioPacket(byte[] packet)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                {
                    return;
                }
                
                var arrivalTime = DateTime.UtcNow;
                _lastVideoOrAudioPacketTime = DateTime.UtcNow;
                Interlocked.Increment(ref _realAudioPacketsWindow);
                _currentAudioFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "audio", _currentAudioFrameIndex);
                
                SendAudioPacketInternal(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send audio packet");
            }
        }

        public void ResetAudioDecoder(int lostFrames = 0)
        {
            lock (_opusDecoderLock)
            {
                try
                {
                    _opusDecoder?.Dispose();
                    _opusDecoder = null;
                    
                    // ✅ 修复爆音问题：根据丢失的帧数动态调整处理策略
                    // 关键问题：当丢失大量帧时，时间戳虽然连续，但实际音频数据已经跳过了大量帧
                    // 这会导致浏览器端音频缓冲区不连续，引起爆音
                    
                    if (_audioTimestamp == 0)
                    {
                        // 首次初始化：使用当前时间
                        var now = DateTime.UtcNow;
                        var timeSinceStart = (now - _epochStart).TotalSeconds;
                        _audioTimestamp = (uint)(timeSinceStart * AUDIO_CLOCK_RATE);
                        _logger.LogDebug("🔄 Audio timestamp initialized: {Timestamp}", _audioTimestamp);
                    }
                    else
                    {
                        // ✅ 修复爆音问题：保持时间戳连续性，避免时间戳跳跃
                        // 关键：RTP时间戳不能回退，必须保持单调递增
                        // 即使丢失了大量帧，也要保持时间戳连续性，通过跳过更多帧来清空浏览器端缓冲区
                        // 这样浏览器端会自然丢弃旧的音频数据，新数据会平滑接续
                        _logger.LogDebug("🔄 Detected frame loss ({LostFrames} frames), keeping timestamp continuous at {Timestamp}; subsequent frames increment normally and frames are skipped to drain the buffer", 
                            lostFrames, _audioTimestamp);
                    }
                    
                    // ✅ 修复爆音问题：根据丢失的帧数动态调整跳过的帧数
                    // 问题：当丢失大量帧时，浏览器端音频缓冲区可能还有旧的音频数据
                    // 解决方案：根据丢失的帧数动态调整跳过的帧数
                    // - 少量丢失（<50帧）：跳过3-5帧
                    // - 中等丢失（50-200帧）：跳过5-10帧
                    // - 大量丢失（>200帧）：跳过10-20帧，但不超过20帧以避免长时间静音
                    _audioResetting = true;
                    int skipFrames;
                    if (lostFrames <= 0)
                    {
                        skipFrames = Math.Max(AUDIO_RESYNC_FRAMES, 3); // 默认至少跳过3帧
                    }
                    else if (lostFrames < 50)
                    {
                        skipFrames = Math.Min(5, Math.Max(3, lostFrames / 10)); // 3-5帧
                    }
                    else if (lostFrames < 200)
                    {
                        skipFrames = Math.Min(10, Math.Max(5, lostFrames / 20)); // 5-10帧
                    }
                    else
                    {
                        skipFrames = Math.Min(20, Math.Max(10, lostFrames / 30)); // 10-20帧，但不超过20
                    }
                    _audioFramesToSkip = skipFrames;
                    
                    _logger.LogWarning("🔄 Audio decoder reset (detected {LostFrames} lost frames), timestamp {Timestamp}; skipping {SkipFrames} frames to drain the browser buffer and resync",
                        lostFrames, _audioTimestamp, _audioFramesToSkip);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to reset audio decoder");
                    _opusDecoder = null;
                }
            }
        }
        
        private void SendAudioPacketInternal(byte[] packet)
        {
            try
            {
                if (_peerConnection == null || packet == null || packet.Length <= 1)
                {
                    return;
                }
                
                var payloadType = (HeaderType)packet[0];
                if (payloadType != HeaderType.AUDIO)
                {
                    _logger.LogWarning("⚠️ Non-audio packet passed to OnAudioPacket, ignored");
                    return;
                }

                // ✅ 如果正在重置音频，跳过几帧以重新同步，避免爆音
                if (_audioResetting)
                {
                    if (_audioFramesToSkip > 0)
                    {
                        _audioFramesToSkip--;
                        if (_audioFramesToSkip == 0)
                        {
                            _audioResetting = false;
                            _logger.LogInformation("✅ Audio resync complete, resuming normal sending");
                        }
                        else
                        {
                            _logger.LogDebug("⏭️ Skipping audio frame to resync, {Remaining} frames remaining", _audioFramesToSkip);
                        }
                        return; // 跳过此帧
                    }
                }

                var opusFrame = packet.AsSpan(1).ToArray();
                TrackHostSilence(opusFrame.Length);

                if (_forceStereoDownmix)
                {
                    if (TrySendOpusDownmixedToStereo(opusFrame, out var downmixedFrame))
                    {
                        SendAudioOpusDirect(downmixedFrame.FrameData, downmixedFrame.SamplesPerFrame);
                    }
                    else
                    {
                        SendAudioOpusDirect(opusFrame);
                    }
                }
                else
                {
                    SendAudioOpusDirect(opusFrame);
                }

                _latencyStats?.RecordPacketSent(_sessionId, "audio", _currentAudioFrameIndex);
                _audioPacketCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to process audio packet");
            }
        }

        // 主机编码静音时 Opus 帧只有几个字节；持续一段时间说明主机那边没有声音输出（不是转发出了问题）
        private const int SILENT_OPUS_FRAME_MAX_BYTES = 4;
        private static readonly TimeSpan HostSilenceWarnAfter = TimeSpan.FromSeconds(3);
        private DateTime _hostSilenceSince = DateTime.MinValue;
        private bool _hostSilenceWarned;

        private void TrackHostSilence(int opusFrameLength)
        {
            var now = DateTime.UtcNow;
            if (opusFrameLength > SILENT_OPUS_FRAME_MAX_BYTES)
            {
                if (_hostSilenceWarned)
                {
                    _logger.LogDebug("🔊 Host audio resumed after {Seconds:F1}s of silence", (now - _hostSilenceSince).TotalSeconds);
                }
                _hostSilenceSince = DateTime.MinValue;
                _hostSilenceWarned = false;
                return;
            }

            if (_hostSilenceSince == DateTime.MinValue)
            {
                _hostSilenceSince = now;
            }
            else if (!_hostSilenceWarned && now - _hostSilenceSince > HostSilenceWarnAfter)
            {
                _hostSilenceWarned = true;
                _logger.LogDebug("🔇 Host has been sending silent audio frames ({Bytes} bytes) for over {Seconds}s",
                    opusFrameLength, HostSilenceWarnAfter.TotalSeconds);
            }
        }

        private static readonly TimeSpan AudioSummaryInterval = TimeSpan.FromSeconds(5);
        private long _realAudioPacketsWindow;
        private long _silentAudioPacketsWindow;
        private DateTime _lastAudioSummaryTime = DateTime.UtcNow;

        /// <summary>
        /// 每 5 秒汇总一次：收到的主机音频包数 vs 因没有音频而补发的静音包数
        /// </summary>
        private void LogAudioSendSummaryIfDue(DateTime now)
        {
            if (now - _lastAudioSummaryTime < AudioSummaryInterval)
            {
                return;
            }
            _lastAudioSummaryTime = now;

            long real = Interlocked.Exchange(ref _realAudioPacketsWindow, 0);
            long silent = Interlocked.Exchange(ref _silentAudioPacketsWindow, 0);
            _logger.LogDebug("🔊 Audio send (5s): host packets={Real}, keepalive silence={Silent}, total host packets={Total}",
                real, silent, _audioPacketCount);
        }

        private void SendAudioOpusDirect(byte[] opusFrame, int? samplesPerFrameOverride = null, bool isKeepaliveSilence = false)
        {
            try
            {
                if (_peerConnection == null || opusFrame == null || opusFrame.Length == 0)
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
                
                if (!canSend)
                {
                    if (_audioPacketCount < 10 || _audioPacketCount % 100 == 0)
                    {
                        _logger.LogDebug("⏳ Waiting for audio channel to be ready: signaling={Signaling}, connection={Connection}, ICE={Ice}", 
                            signalingState, connectionState, iceState);
                    }
                    return;
                }
                
                int samplesPerFrame = samplesPerFrameOverride ?? _audioFrameSize;
                if (samplesPerFrame <= 0)
                {
                    samplesPerFrame = _audioFrameSize > 0 ? _audioFrameSize : 480;
                }
                uint currentTimestamp = _audioTimestamp;
                _audioTimestamp += (uint)samplesPerFrame;
                
                ushort currentSeqNum = (ushort)(_audioSequenceNumber & 0xFFFF);
                _audioSequenceNumber++;
                
                var rtpPacket = new RTPPacket(12 + opusFrame.Length);
                rtpPacket.Header.Version = 2;
                rtpPacket.Header.PayloadType = 111;
                rtpPacket.Header.SequenceNumber = currentSeqNum;
                rtpPacket.Header.Timestamp = currentTimestamp;
                rtpPacket.Header.SyncSource = _audioSsrc;
                rtpPacket.Header.MarkerBit = 0;
                
                System.Buffer.BlockCopy(opusFrame, 0, rtpPacket.Payload, 0, opusFrame.Length);
                
                byte[] rtpBytes = rtpPacket.GetBytes();
                
                if (!isKeepaliveSilence && (_audioPacketCount < 10 || _audioPacketCount % 100 == 0))
                {
                    _logger.LogDebug("📤 Sending Opus RTP packet: seq={Seq}, ts={Ts}, samples={Samples}, size={Size} bytes", 
                        currentSeqNum, currentTimestamp, samplesPerFrame, opusFrame.Length);
                }
                
                SendAudioRTPRaw(rtpBytes, opusFrame, 111);
                
                // ✅ 更新最后真实音频包发送时间（用于 keepalive 判断）
                _lastAudioPacketTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send Opus data");
            }
        }

        private bool TrySendOpusReencoded(byte[] originalOpusFrame)
        {
            try
            {
                if (_peerConnection == null || originalOpusFrame == null || originalOpusFrame.Length == 0)
                {
                    return false;
                }
                
                SendAudioOpusDirect(originalOpusFrame);
                
                if (_audioPacketCount < 10)
                {
                    _logger.LogInformation("✅ Sending Opus for higher audio quality even though browser selected PCMU");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                if (_audioPacketCount < 10)
                {
                    _logger.LogWarning(ex, "⚠️ Opus re-encoding failed, falling back to transcoding");
                }
                return false;
            }
        }
        
        private bool TrySendOpusDownmixedToStereo(byte[] opusFrame, out DownmixedOpusFrame downmixedFrame)
        {
            downmixedFrame = default;
            
            try
            {
                if (opusFrame == null || opusFrame.Length == 0)
                {
                    return false;
                }

                if (_audioFrameSize <= 0 || _audioSampleRate <= 0 || _audioChannels <= 0)
                {
                    return false;
                }

                float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                int samplesDecoded;

                lock (_opusDecoderLock)
                {
                    if (_opusDecoder == null)
                    {
                        _opusDecoder = OpusCodecFactory.CreateDecoder(_audioSampleRate, _audioChannels);
                        _logger.LogInformation("✅ Audio downmix: initialized Opus decoder {Rate}Hz / {Channels}ch", _audioSampleRate, _audioChannels);
                    }

                    samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);
                }

                if (samplesDecoded <= 0)
                {
                    if (_audioPacketCount < 5)
                    {
                        _logger.LogWarning("⚠️ Audio downmix: decode returned 0 samples");
                    }
                    return false;
                }

                int stereoSamples = samplesDecoded;
                short[] stereoSamplesBuffer = ArrayPool<short>.Shared.Rent(stereoSamples * 2);

                try
                {
                    var stereoSpan = stereoSamplesBuffer.AsSpan(0, stereoSamples * 2);
                    if (!TryBuildStereoSamples(pcmBufferFloat, stereoSamples, _audioChannels, stereoSpan))
                    {
                        if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                        {
                            _logger.LogWarning("⚠️ Audio downmix: invalid channel matrix (channels={Channels}), skipping downmix", _audioChannels);
                        }
                        return false;
                    }

                    byte[] encodeBuffer = ArrayPool<byte>.Shared.Rent(_opusEncodeBuffer.Length);

                    try
                    {
                        int encodedBytes;
                        lock (_opusEncoderLock)
                        {
                            if (_stereoOpusEncoder == null || _stereoEncoderSampleRate != _audioSampleRate)
                            {
                                _stereoOpusEncoder?.Dispose();
                                _stereoOpusEncoder = new OpusEncoder(_audioSampleRate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
                                _stereoEncoderSampleRate = _audioSampleRate;
                                _stereoOpusEncoder.Bitrate = Math.Min(256000, _audioSampleRate * 4);
                                _logger.LogInformation("✅ Audio downmix: initialized stereo Opus encoder {Rate}Hz / 2ch", _audioSampleRate);
                            }

                            encodedBytes = _stereoOpusEncoder.Encode(stereoSamplesBuffer, 0, stereoSamples, encodeBuffer, 0, encodeBuffer.Length);
                        }

                        if (encodedBytes <= 0)
                        {
                            if (_audioPacketCount < 5)
                            {
                                _logger.LogWarning("⚠️ Audio downmix: Opus encode failed, returned {Bytes} bytes", encodedBytes);
                            }
                            return false;
                        }

                        var downmixedData = new byte[encodedBytes];
                        System.Buffer.BlockCopy(encodeBuffer, 0, downmixedData, 0, encodedBytes);
                        downmixedFrame = new DownmixedOpusFrame(downmixedData, stereoSamples);
                        return true;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(encodeBuffer);
                    }
                }
                finally
                {
                    ArrayPool<short>.Shared.Return(stereoSamplesBuffer);
                }
            }
            catch (Exception ex)
            {
                if (_audioPacketCount < 5 || _audioPacketCount % 100 == 0)
                {
                    _logger.LogWarning(ex, "⚠️ Audio downmix failed, falling back to original audio");
                }
                downmixedFrame = default;
                return false;
            }
        }
        
        private bool TryBuildStereoSamples(float[] source, int samples, int sourceChannels, Span<short> destination)
        {
            if (destination.Length < samples * 2)
            {
                return false;
            }

            if (sourceChannels <= 0 || samples <= 0)
            {
                return false;
            }

            if (sourceChannels == 1)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    float value = Math.Clamp(source[sample], -1f, 1f);
                    short converted = (short)Math.Round(value * 32767f);
                    destination[sample * 2] = converted;
                    destination[sample * 2 + 1] = converted;
                }
                return true;
            }

            var matrix = BuildDownmixMatrix(sourceChannels);
            if (!matrix.IsValid || matrix.Left.Length != sourceChannels || matrix.Right.Length != sourceChannels)
            {
                return false;
            }

            var floatSpan = source.AsSpan();
            var leftWeights = matrix.Left;
            var rightWeights = matrix.Right;
            float normalization = matrix.Normalization;

            for (int sample = 0; sample < samples; sample++)
            {
                int baseIndex = sample * sourceChannels;
                float leftValue = 0f;
                float rightValue = 0f;

                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    float value = floatSpan[baseIndex + ch];
                    leftValue += value * leftWeights[ch];
                    rightValue += value * rightWeights[ch];
                }

                leftValue *= normalization;
                rightValue *= normalization;

                float peak = Math.Max(Math.Abs(leftValue), Math.Abs(rightValue));
                if (peak > 1f)
                {
                    float scale = 1f / peak;
                    leftValue *= scale;
                    rightValue *= scale;
                }

                leftValue = Math.Clamp(leftValue, -1f, 1f);
                rightValue = Math.Clamp(rightValue, -1f, 1f);

                destination[sample * 2] = (short)Math.Round(leftValue * 32767f);
                destination[sample * 2 + 1] = (short)Math.Round(rightValue * 32767f);
            }

            return true;
        }

        private readonly struct DownmixedOpusFrame
        {
            public DownmixedOpusFrame(byte[] frameData, int samplesPerFrame)
            {
                FrameData = frameData;
                SamplesPerFrame = samplesPerFrame;
            }

            public byte[] FrameData { get; }
            public int SamplesPerFrame { get; }
            public bool IsValid => FrameData != null && FrameData.Length > 0 && SamplesPerFrame > 0;
        }

        private readonly struct DownmixMatrix
        {
            public DownmixMatrix(float[] left, float[] right, float normalization)
            {
                Left = left;
                Right = right;
                Normalization = normalization;
            }

            public float[] Left { get; }
            public float[] Right { get; }
            public float Normalization { get; }
            public bool IsValid => Left.Length > 0 && Right.Length > 0;
        }

        private static DownmixMatrix BuildDownmixMatrix(int channels)
        {
            if (channels <= 0)
            {
                return new DownmixMatrix(Array.Empty<float>(), Array.Empty<float>(), 1f);
            }

            const float INV_SQRT2 = 0.70710677f;
            const float LFE_GAIN = 0.5f;
            const float SURROUND_GAIN = 0.70710677f;
            const float DIRECT_GAIN = 1f;

            var left = new float[channels];
            var right = new float[channels];

            switch (channels)
            {
                case 1:
                    left[0] = DIRECT_GAIN;
                    right[0] = DIRECT_GAIN;
                    break;
                case 2:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    break;
                case 3:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    break;
                case 4:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = SURROUND_GAIN;
                    right[3] = SURROUND_GAIN;
                    break;
                case 5:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = SURROUND_GAIN;
                    right[4] = SURROUND_GAIN;
                    break;
                case 6:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    left[4] = SURROUND_GAIN;
                    right[5] = SURROUND_GAIN;
                    break;
                case 7:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    left[4] = SURROUND_GAIN;
                    right[5] = SURROUND_GAIN;
                    left[6] = SURROUND_GAIN;
                    right[6] = SURROUND_GAIN;
                    break;
                default:
                    left[0] = DIRECT_GAIN;
                    right[1] = DIRECT_GAIN;
                    left[2] = INV_SQRT2;
                    right[2] = INV_SQRT2;
                    left[3] = LFE_GAIN;
                    right[3] = LFE_GAIN;
                    if (channels > 4)
                    {
                        left[4] = SURROUND_GAIN;
                    }
                    if (channels > 5)
                    {
                        right[5] = SURROUND_GAIN;
                    }
                    if (channels > 6)
                    {
                        left[6] = SURROUND_GAIN;
                    }
                    if (channels > 7)
                    {
                        right[7] = SURROUND_GAIN;
                    }
                    for (int ch = 8; ch < channels; ch++)
                    {
                        if ((ch & 1) == 0)
                        {
                            left[ch] = SURROUND_GAIN;
                        }
                        else
                        {
                            right[ch] = SURROUND_GAIN;
                        }
                    }
                    break;
            }

            float sumLeft = 0f;
            float sumRight = 0f;
            for (int i = 0; i < channels; i++)
            {
                sumLeft += Math.Abs(left[i]);
                sumRight += Math.Abs(right[i]);
            }

            float maxSum = Math.Max(sumLeft, sumRight);
            float normalization = maxSum > 1f ? 1f / maxSum : 1f;

            return new DownmixMatrix(left, right, normalization);
        }

        private static int ParseAudioChannels(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 2)
            {
                int be = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0, 2));
                if (IsValidChannelCount(be)) return be;

                int le = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
                if (IsValidChannelCount(le)) return le;
            }

            if (span.Length >= 1 && IsValidChannelCount(span[0]))
            {
                return span[0];
            }

            return 2;
        }

        private static int ParseBitsPerSample(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 8)
            {
                int be = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6, 2));
                if (IsValidBitsPerSample(be)) return be;

                int le = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2));
                if (IsValidBitsPerSample(le)) return le;
            }

            if (span.Length > 6 && IsValidBitsPerSample(span[6]))
            {
                return span[6];
            }

            return 16;
        }

        private static int ParseSampleRate(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 6)
            {
                int be = BinaryPrimitives.ReadInt32BigEndian(span.Slice(2, 4));
                if (IsValidSampleRate(be)) return be;

                int le = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(2, 4));
                if (IsValidSampleRate(le)) return le;
            }

            return 48000;
        }

        private static int ParseFrameSize(byte[] header)
        {
            var span = header.AsSpan();

            if (span.Length >= 12)
            {
                int be32 = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
                if (IsValidFrameSize(be32)) return be32;

                int le32 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
                if (IsValidFrameSize(le32)) return le32;
            }

            if (span.Length >= 10)
            {
                int be16 = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(8, 2));
                if (IsValidFrameSize(be16)) return be16;

                int le16 = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
                if (IsValidFrameSize(le16)) return le16;
            }

            return 480;
        }

        private static bool IsValidChannelCount(int value) => value >= 1 && value <= 8;

        private static bool IsValidBitsPerSample(int value) => value is 8 or 16 or 24 or 32;

        private static bool IsValidSampleRate(int value) => value >= 8000 && value <= 192000;

        private static bool IsValidFrameSize(int value) => value >= 60 && value <= 8192;
    }
}

