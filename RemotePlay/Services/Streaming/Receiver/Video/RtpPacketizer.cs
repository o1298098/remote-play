using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// RTP 封装器 - 负责 NAL 解析、分片和 RTP 封装
    /// 参照现有逻辑，但改为异步实现
    /// </summary>
    internal class RtpPacketizer
    {
        private readonly ILogger? _logger;
        private readonly ReflectionMethodCache? _methodCache;
        private readonly string _detectedVideoFormat;
        private readonly int _negotiatedPtH264;
        private readonly int _negotiatedPtHevc;
        
        private const int RTP_MTU = 1200;
        
        private uint _sequenceNumber = 0;
        private readonly object _sequenceLock = new();
        
        public RtpPacketizer(
            ILogger? logger,
            ReflectionMethodCache? methodCache,
            string detectedVideoFormat,
            int negotiatedPtH264,
            int negotiatedPtHevc)
        {
            _logger = logger;
            _methodCache = methodCache;
            _detectedVideoFormat = detectedVideoFormat ?? "h264";
            _negotiatedPtH264 = negotiatedPtH264;
            _negotiatedPtHevc = negotiatedPtHevc;
        }
        
        /// <summary>
        /// 解析 Annex-B 格式的 NAL units
        /// </summary>
        public List<byte[]> ParseAnnexBNalUnits(byte[] data)
        {
            var nalUnits = new List<byte[]>();
            if (data == null || data.Length < 4) return nalUnits;

            Span<byte> dataSpan = data;
            int currentPos = 0;

            while (currentPos < dataSpan.Length - 3)
            {
                int startCodePos = -1;
                int startCodeLength = 0;

                // 查找起始码 0x000001 或 0x00000001
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

                // 查找下一个起始码
                int nextStartCodePos = -1;
                int searchStart = startCodePos + startCodeLength;

                for (int i = searchStart; i < dataSpan.Length - 3; i++)
                {
                    if (dataSpan[i] == 0x00 && dataSpan[i + 1] == 0x00)
                    {
                        if (i + 3 < dataSpan.Length && dataSpan[i + 2] == 0x00 && dataSpan[i + 3] == 0x01)
                        {
                            nextStartCodePos = i;
                            break;
                        }
                        else if (i + 2 < dataSpan.Length && dataSpan[i + 2] == 0x01)
                        {
                            nextStartCodePos = i;
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
        
        /// <summary>
        /// 异步发送单个 NAL unit
        /// </summary>
        public async Task<bool> SendSingleNalUnitAsync(
            byte[] nalUnit, 
            uint timestamp, 
            uint ssrc, 
            bool isFrameEnd)
        {
            if (nalUnit == null || nalUnit.Length == 0 || _methodCache == null) return false;

            try
            {
                bool sent = await _methodCache.InvokeSendVideoAsync(timestamp, nalUnit, timeoutMs: 200, maxRetries: 2);
                if (sent) return true;
                
                var rtpPacket = new RTPPacket(12 + nalUnit.Length);
                rtpPacket.Header.Version = 2;
                int payloadType = _detectedVideoFormat == "hevc" ? _negotiatedPtHevc : _negotiatedPtH264;
                rtpPacket.Header.PayloadType = (byte)payloadType;
                
                ushort seqNum;
                lock (_sequenceLock)
                {
                    seqNum = (ushort)_sequenceNumber;
                    _sequenceNumber++;
                    if (_sequenceNumber > 0xFFFF) _sequenceNumber = 0;
                }
                
                rtpPacket.Header.SequenceNumber = seqNum;
                rtpPacket.Header.Timestamp = timestamp;
                rtpPacket.Header.SyncSource = ssrc;
                rtpPacket.Header.MarkerBit = isFrameEnd ? 1 : 0;
                System.Buffer.BlockCopy(nalUnit, 0, rtpPacket.Payload, 0, nalUnit.Length);
                
                       return await _methodCache.InvokeSendRtpRawAsync(
                           SDPMediaTypesEnum.video, rtpPacket.GetBytes(), timestamp, isFrameEnd ? 1 : 0, payloadType,
                           timeoutMs: 200, maxRetries: 2);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SendSingleNalUnitAsync 异常");
                return false;
            }
        }
        
        /// <summary>
        /// 异步发送分片 NAL unit (FU-A 分片)
        /// </summary>
        public async Task<bool> SendFragmentedNalUnitAsync(
            byte[] nalUnit,
            uint timestamp,
            uint ssrc)
        {
            if (nalUnit == null || nalUnit.Length == 0) return false;

            byte nalType = (byte)(nalUnit[0] & 0x1F);
            byte nalHeader = (byte)(nalUnit[0] & 0x60);

            int maxFragmentSize = RTP_MTU - 12 - 2; // RTP header + FU-A header
            int fragmentCount = (nalUnit.Length + maxFragmentSize - 1) / maxFragmentSize;

            for (int i = 0; i < fragmentCount; i++)
            {
                int fragmentStart = i * maxFragmentSize;
                int fragmentLength = Math.Min(maxFragmentSize, nalUnit.Length - fragmentStart);

                try
                {
                    byte fuIndicator = (byte)(nalHeader | 28);
                    byte fuHeader = nalType;
                    if (i == 0) fuHeader |= 0x80;
                    else if (i == fragmentCount - 1) fuHeader |= 0x40;

                    var payload = new byte[2 + fragmentLength];
                    payload[0] = fuIndicator;
                    payload[1] = fuHeader;
                    System.Buffer.BlockCopy(nalUnit, fragmentStart, payload, 2, fragmentLength);

                    if (_methodCache == null) return false;
                    
                    int payloadType = _detectedVideoFormat == "hevc" ? _negotiatedPtHevc : _negotiatedPtH264;
                    bool sent = await _methodCache.InvokeSendRtpRawAsync(
                        SDPMediaTypesEnum.video, payload, timestamp, (i == fragmentCount - 1) ? 1 : 0, payloadType,
                        timeoutMs: 200, maxRetries: 2);
                    
                    if (!sent) return false;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "发送分片失败: fragment {I}/{Count}", i + 1, fragmentCount);
                    return false;
                }
            }

            return true;
        }
        
        /// <summary>
        /// 异步发送视频数据（自动处理 NAL 解析和分片）
        /// </summary>
        public async Task<bool> SendVideoDataAsync(
            byte[] data,
            uint timestamp,
            uint ssrc)
        {
            if (data == null || data.Length == 0) return false;

            try
            {
                var nalUnits = ParseAnnexBNalUnits(data);
                if (nalUnits.Count == 0)
                {
                    return await SendSingleNalUnitAsync(data, timestamp, ssrc, true);
                }

                for (int i = 0; i < nalUnits.Count; i++)
                {
                    var nalUnit = nalUnits[i];
                    if (nalUnit.Length == 0) continue;

                    bool isLastNal = (i == nalUnits.Count - 1);
                    bool sent = nalUnit.Length > RTP_MTU - 12
                        ? await SendFragmentedNalUnitAsync(nalUnit, timestamp, ssrc)
                        : await SendSingleNalUnitAsync(nalUnit, timestamp, ssrc, isLastNal);
                    
                    if (!sent) return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SendVideoDataAsync 异常");
                return false;
            }
        }
    }
}

