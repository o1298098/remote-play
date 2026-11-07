using System;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed partial class WebRTCReceiver
    {
        /// <summary>
        /// 关键帧入口（OutputPipeline 已判定为关键帧）
        /// </summary>
        public void OnVideoPacketPriority(byte[] packet)
        {
            HandleVideoPacket(packet, isKeyFrameHint: true);
        }

        public void OnVideoPacket(byte[] packet)
        {
            HandleVideoPacket(packet, isKeyFrameHint: false);
        }

        private void HandleVideoPacket(byte[] packet, bool isKeyFrameHint)
        {
            try
            {
                if (_disposed || packet == null || packet.Length <= 1 || _peerConnection == null)
                {
                    return;
                }

                _currentVideoFrameIndex++;
                _latencyStats?.RecordPacketArrival(_sessionId, "video", _currentVideoFrameIndex);

                var pipeline = _videoPipeline;
                if (pipeline != null)
                {
                    pipeline.OnFrame(packet, isKeyFrameHint);
                    Interlocked.Increment(ref _videoPacketCount);
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastVideoPipelineWarningTime).TotalSeconds < VIDEO_PIPELINE_WARNING_INTERVAL_SECONDS)
                {
                    return;
                }
                _lastVideoPipelineWarningTime = now;

                if (_videoTrack != null)
                {
                    _logger.LogWarning("⚠️ 视频管道未初始化，尝试重新初始化 (已收到 {Count} 个视频包)", _videoPacketCount);
                    InitializeVideoPipeline();
                }
                else
                {
                    _logger.LogError("❌ 视频管道未初始化：_videoTrack 为 null (已收到 {Count} 个视频包)", _videoPacketCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理视频包失败: packetLen={Len}, count={Count}",
                    packet?.Length ?? 0, _videoPacketCount);
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
    }
}
