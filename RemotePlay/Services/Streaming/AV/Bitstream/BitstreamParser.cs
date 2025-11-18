using Microsoft.Extensions.Logging;
using System;

namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// Bitstream 解析器 - 解析 H.264/H.265 slice header
    /// 
    /// 注意：这是一个简化实现，完整实现需要复杂的 RBSP 解析
    /// 当前实现主要关注 P 帧的 reference_frame 字段
    /// </summary>
    public class BitstreamParser
    {
        private readonly ILogger<BitstreamParser>? _logger;
        private readonly string _codec; // "h264" 或 "hevc"

        public BitstreamParser(string codec, ILogger<BitstreamParser>? logger = null)
        {
            _codec = codec?.ToLowerInvariant() ?? "h264";
            _logger = logger;
        }

        /// <summary>
        /// 解析 slice header，提取 slice_type 和 reference_frame
        /// </summary>
        public bool ParseSlice(byte[] frameData, out BitstreamSlice slice)
        {
            slice = new BitstreamSlice();

            if (frameData == null || frameData.Length < 10)
            {
                return false;
            }

            try
            {
                if (_codec == "h264" || _codec == "avc")
                {
                    return ParseSliceH264(frameData, out slice);
                }
                else if (_codec == "hevc" || _codec == "h265")
                {
                    return ParseSliceH265(frameData, out slice);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "解析 slice header 失败");
            }

            return false;
        }

        /// <summary>
        /// 解析 H.264 slice header
        /// </summary>
        private bool ParseSliceH264(byte[] frameData, out BitstreamSlice slice)
        {
            slice = new BitstreamSlice();

            // 查找 startcode (0x00 0x00 0x00 0x01 或 0x00 0x00 0x01)
            int offset = FindStartCode(frameData);
            if (offset < 0 || offset + 5 >= frameData.Length)
                return false;

            // 跳过 startcode
            if (frameData[offset + 3] == 0x01)
                offset += 4;
            else if (offset + 4 < frameData.Length && frameData[offset + 4] == 0x01)
                offset += 5;
            else
                return false;

            if (offset >= frameData.Length)
                return false;

            // NAL unit type
            byte nalType = (byte)(frameData[offset] & 0x1F);

            // NAL type 1 = non-IDR slice, 5 = IDR slice
            if (nalType == 5)
            {
                slice.SliceType = SliceType.I;
                slice.ReferenceFrame = 0;
                return true;
            }
            else if (nalType == 1)
            {
                slice.SliceType = SliceType.P;
                // 对于 H.264，reference_frame 需要从 slice header 中解析
                // 这是一个简化实现，实际需要解析 RBSP
                // 默认假设 reference_frame = 0（前一帧）
                slice.ReferenceFrame = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 解析 H.265/HEVC slice header
        /// </summary>
        private bool ParseSliceH265(byte[] frameData, out BitstreamSlice slice)
        {
            slice = new BitstreamSlice();

            // 查找 startcode
            int offset = FindStartCode(frameData);
            if (offset < 0 || offset + 5 >= frameData.Length)
                return false;

            // 跳过 startcode
            if (frameData[offset + 3] == 0x01)
                offset += 4;
            else if (offset + 4 < frameData.Length && frameData[offset + 4] == 0x01)
                offset += 5;
            else
                return false;

            if (offset >= frameData.Length)
                return false;

            // NAL unit type (6 bits)
            byte nalType = (byte)((frameData[offset] >> 1) & 0x3F);

            // NAL type 19 = IDR, 1 = non-IDR slice
            if (nalType == 19 || nalType == 20)
            {
                slice.SliceType = SliceType.I;
                slice.ReferenceFrame = 0xFF; // I 帧没有参考帧
                return true;
            }
            else if (nalType == 1)
            {
                slice.SliceType = SliceType.P;
                // 对于 H.265，reference_frame 需要从 slice header 中解析
                // 这是一个简化实现
                slice.ReferenceFrame = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 查找 startcode (0x00 0x00 0x00 0x01 或 0x00 0x00 0x01)
        /// </summary>
        private int FindStartCode(byte[] data)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x00)
                {
                    if (data[i + 2] == 0x01)
                        return i;
                    if (i + 3 < data.Length && data[i + 2] == 0x00 && data[i + 3] == 0x01)
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 修改 slice 中的参考帧索引
        /// 注意：这是一个占位实现，完整的实现需要修改 RBSP 编码
        /// 
        /// 由于 bitstream 修改非常复杂，当前实现返回 false
        /// 实际应用中，如果解码器支持，可以在解码前进行修改
        /// </summary>
        public bool SetReferenceFrame(byte[] frameData, uint newReferenceFrame, out byte[] modifiedFrameData)
        {
            modifiedFrameData = frameData;
            // TODO: 实现完整的 bitstream 修改逻辑
            // 这需要：
            // 1. 解析 RBSP
            // 2. 修改 reference_frame 字段
            // 3. 重新编码 RBSP
            // 
            // 由于复杂度高，当前返回 false，表示不支持修改
            // 在实际应用中，可以考虑：
            // - 使用 FFmpeg 的 bitstream filter
            // - 或者依赖解码器的容错能力
            _logger?.LogWarning("Bitstream 参考帧修改功能未完全实现，需要完整的 RBSP 编码/解码");
            return false;
        }
    }
}

