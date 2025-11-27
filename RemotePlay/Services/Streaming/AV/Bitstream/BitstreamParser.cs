using Microsoft.Extensions.Logging;
using System;

namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// Bitstream 解析器 - 解析 H.264/H.265 slice header
    /// 
    /// 参考 chiaki 的 bitstream.c 实现
    /// 支持：
    /// 1. 解析 SPS (Sequence Parameter Set) 获取关键参数
    /// 2. 解析 Slice header 获取 slice_type 和 reference_frame
    /// 3. 修改 H.265 P 帧的参考帧索引
    /// </summary>
    public class BitstreamParser
    {
        private readonly ILogger<BitstreamParser>? _logger;
        private readonly string _codec; // "h264" 或 "hevc"
        
        // H.264 SPS 参数
        private uint _h264Log2MaxFrameNumMinus4 = 0;
        
        // H.265 SPS 参数
        private uint _h265Log2MaxPicOrderCntLsbMinus4 = 0;

        public BitstreamParser(string codec, ILogger<BitstreamParser>? logger = null)
        {
            _codec = codec?.ToLowerInvariant() ?? "h264";
            _logger = logger;
        }

        /// <summary>
        /// 解析视频头部（SPS），提取关键参数
        /// </summary>
        public bool ParseHeader(byte[] headerData)
        {
            if (headerData == null || headerData.Length < 10)
                return false;

            try
            {
                if (_codec == "h264" || _codec == "avc")
                {
                    return ParseHeaderH264(headerData);
                }
                else if (_codec == "hevc" || _codec == "h265")
                {
                    return ParseHeaderH265(headerData);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "解析视频头部失败");
            }

            return false;
        }

        /// <summary>
        /// 解析 H.264 SPS
        /// </summary>
        private bool ParseHeaderH264(byte[] data)
        {
            var vlc = new VlcParser(data, 0, data.Length, null);
            
            if (!SkipStartCode(vlc))
            {
                _logger?.LogWarning("parse_sps_h264: No startcode found");
                return false;
            }

            vlc.EatBits(1); // forbidden_zero_bit
            vlc.EatBits(2); // nal_ref_idc
            uint nalUnitType = vlc.GetUimsbf(5);

            if (nalUnitType != 7) // SPS
            {
                _logger?.LogWarning("parse_sps_h264: Unexpected NAL unit type {NalType}", nalUnitType);
                return false;
            }

            var rbsp = new RbspParser(vlc, 0xFFFFFFFF, null);

            uint profileIdc = rbsp.ReadU(8);
            rbsp.ReadU(6); // constraint_set_flags
            rbsp.ReadU(2); // reserved_zero_2bits
            rbsp.ReadU(8); // level_idc
            rbsp.ReadUE(); // seq_parameter_set_id

            if (profileIdc == 100 || profileIdc == 110 ||
                profileIdc == 122 || profileIdc == 244 || profileIdc == 44 ||
                profileIdc == 83 || profileIdc == 86 || profileIdc == 118 ||
                profileIdc == 128 || profileIdc == 138 || profileIdc == 139 ||
                profileIdc == 134 || profileIdc == 135)
            {
                if (rbsp.ReadUE() == 3) // chroma_format_idc
                    rbsp.ReadU(1); // separate_colour_plane_flag

                rbsp.ReadUE(); // bit_depth_luma_minus8
                rbsp.ReadUE(); // bit_depth_chroma_minus8
                rbsp.ReadU(1); // qpprime_y_zero_transform_bypass_flag

                if (rbsp.ReadU(1) != 0) // seq_scaling_matrix_present_flag
                    return false;
            }

            _h264Log2MaxFrameNumMinus4 = rbsp.ReadUE();
            if (_h264Log2MaxFrameNumMinus4 > 12)
            {
                _logger?.LogWarning("parse_sps_h264: Unexpected log2_max_frame_num_minus4 value {Value}", 
                    _h264Log2MaxFrameNumMinus4);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 解析 H.265 SPS
        /// </summary>
        private bool ParseHeaderH265(byte[] data)
        {
            var vlc = new VlcParser(data, 0, data.Length, null);
            
        sps_start:
            if (!SkipStartCode(vlc))
            {
                _logger?.LogWarning("parse_sps_h265: No startcode found");
                return false;
            }

            vlc.EatBits(1); // forbidden_zero_bit
            uint nalUnitType = vlc.GetUimsbf(6);
            vlc.EatBits(6); // nuh_layer_id
            vlc.EatBits(3); // nuh_temporal_id_plus1

            if (nalUnitType == 32) // VPS
                goto sps_start;

            if (nalUnitType != 33) // SPS
            {
                _logger?.LogWarning("parse_sps_h265: Unexpected NAL unit type {NalType}", nalUnitType);
                return false;
            }

            var rbsp = new RbspParser(vlc, 0xFFFFFFFF, null);

            rbsp.ReadU(4); // sps_video_parameter_set_id
            rbsp.ReadU(3); // sps_max_sub_layers_minus1
            rbsp.ReadU(1); // sps_temporal_id_nesting_flag

            rbsp.ReadU(2); // general_profile_space
            rbsp.ReadU(1); // general_tier_flag
            rbsp.ReadU(5); // general_profile_idc
            rbsp.ReadU(32); // general_profile_compatibility_flag[0-31]
            rbsp.ReadU(1); // general_progressive_source_flag
            rbsp.ReadU(1); // general_interlaced_source_flag
            rbsp.ReadU(1); // general_non_packed_constraint_flag
            rbsp.ReadU(1); // general_frame_only_constraint_flag
            rbsp.ReadU(32); rbsp.ReadU(11); // general_reserved_zero_43bits
            rbsp.ReadU(1); // general_inbld_flag / general_reserved_zero_bit
            rbsp.ReadU(8); // general_level_idc

            rbsp.ReadUE(); // sps_seq_parameter_set_id
            if (rbsp.ReadUE() == 3) // chroma_format_idc
                rbsp.ReadU(1); // separate_colour_plane_flag

            rbsp.ReadUE(); // pic_width_in_luma_samples
            rbsp.ReadUE(); // pic_height_in_luma_samples

            if (rbsp.ReadU(1) != 0) // conformance_window_flag
            {
                rbsp.ReadUE(); // conf_win_left_offset
                rbsp.ReadUE(); // conf_win_right_offset
                rbsp.ReadUE(); // conf_win_top_offset
                rbsp.ReadUE(); // conf_win_bottom_offset
            }

            rbsp.ReadUE(); // bit_depth_luma_minus8
            rbsp.ReadUE(); // bit_depth_chroma_minus8

            _h265Log2MaxPicOrderCntLsbMinus4 = rbsp.ReadUE();
            if (_h265Log2MaxPicOrderCntLsbMinus4 > 12)
            {
                _logger?.LogWarning("parse_sps_h265: Unexpected log2_max_pic_order_cnt_lsb_minus4 value {Value}", 
                    _h265Log2MaxPicOrderCntLsbMinus4);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 跳过 startcode
        /// </summary>
        private bool SkipStartCode(VlcParser vlc)
        {
            vlc.FillBits();
            for (int i = 0; i < 64 && vlc.BitsLeft() >= 32; i++)
            {
                if (vlc.PeekBits(32) == 1)
                    break;
                vlc.EatBits(8);
                vlc.FillBits();
            }
            if (vlc.PeekBits(32) != 1)
                return false;
            vlc.EatBits(32);
            vlc.FillBits();
            return true;
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

            var vlc = new VlcParser(frameData, 0, frameData.Length, null);
            if (!SkipStartCode(vlc))
            {
                _logger?.LogWarning("parse_slice_h264: No startcode found");
                return false;
            }

            vlc.EatBits(1); // forbidden_zero_bit
            vlc.EatBits(2); // nal_ref_idc
            uint nalUnitType = vlc.GetUimsbf(5);

            if (nalUnitType != 1 && nalUnitType != 5)
            {
                _logger?.LogWarning("parse_slice_h264: Unexpected NAL unit type {NalType}", nalUnitType);
                return false;
            }

            // ✅ 检测IDR帧：H.264中nalUnitType == 5 表示IDR
            slice.IsIdr = (nalUnitType == 5);

            var rbsp = new RbspParser(vlc, 0xFFFFFFFF, null);
            rbsp.ReadUE(); // first_mb_in_slice

            uint sliceType = rbsp.ReadUE();
            switch (sliceType)
            {
                case 0:
                case 5:
                    slice.SliceType = SliceType.P;
                    break;
                case 2:
                case 7:
                    slice.SliceType = SliceType.I;
                    break;
                default:
                    slice.SliceType = SliceType.Unknown;
                    break;
            }

            if (nalUnitType == 1) // non-IDR slice
            {
                slice.ReferenceFrame = 0;
                rbsp.ReadUE(); // pic_parameter_set_id
                rbsp.ReadU((int)(_h264Log2MaxFrameNumMinus4 + 4)); // frame_num
                if (rbsp.ReadU(1) != 0) // num_ref_idx_active_override_flag
                {
                    if (rbsp.ReadU(1) != 0) // num_ref_idx_l0_active_override_flag
                        rbsp.ReadUE(); // num_ref_idx_l0_active_minus1
                }
                if (rbsp.ReadU(1) != 0) // ref_pic_list_modification_flag_l0
                {
                    int i = 0;
                    uint modificationOfPicNumsIdc = rbsp.ReadUE();
                    while (i++ < 3)
                    {
                        if (modificationOfPicNumsIdc == 0)
                        {
                            slice.ReferenceFrame = rbsp.ReadUE(); // abs_diff_pic_num_minus1
                        }
                        else if (modificationOfPicNumsIdc < 3)
                        {
                            rbsp.ReadUE(); // abs_diff_pic_num_minus1 or long_term_pic_num
                        }
                        else if (modificationOfPicNumsIdc == 3)
                        {
                            return true;
                        }
                        else
                        {
                            break;
                        }
                        modificationOfPicNumsIdc = rbsp.ReadUE();
                    }
                    _logger?.LogWarning("parse_slice_h264: Failed to parse ref_pic_list_modification");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 解析 H.265/HEVC slice header
        /// </summary>
        private bool ParseSliceH265(byte[] frameData, out BitstreamSlice slice)
        {
            slice = new BitstreamSlice();

            var vlc = new VlcParser(frameData, 0, frameData.Length, null);

            if (!SkipStartCode(vlc))
            {
                _logger?.LogWarning("parse_slice_h265: No startcode found");
                return false;
            }

            vlc.EatBits(1); // forbidden_zero_bit
            uint nalUnitType = vlc.GetUimsbf(6);
            vlc.EatBits(6); // nuh_layer_id
            vlc.EatBits(3); // nuh_temporal_id_plus1

            if (nalUnitType != 1 && nalUnitType != 20)
            {
                _logger?.LogWarning("parse_slice_h265: Unexpected NAL unit type {NalType}", nalUnitType);
                return false;
            }

            // ✅ 检测IDR帧：H.265中nalUnitType == 20 表示IDR
            slice.IsIdr = (nalUnitType == 20);

            var rbsp = new RbspParser(vlc, 0xFFFFFFFF, null);
            uint firstSliceSegmentInPicFlag = rbsp.ReadU(1);
            if (nalUnitType == 20) // IDR
                rbsp.ReadU(1); // no_output_of_prior_pics_flag

            rbsp.ReadUE(); // slice_pic_parameter_set_id
            if (firstSliceSegmentInPicFlag == 0)
                rbsp.ReadUE(); // slice_segment_address

            uint sliceType = rbsp.ReadUE();
            switch (sliceType)
            {
                case 1:
                    slice.SliceType = SliceType.P;
                    break;
                case 2:
                    slice.SliceType = SliceType.I;
                    break;
                default:
                    slice.SliceType = SliceType.Unknown;
                    break;
            }

            if (nalUnitType == 1) // non-IDR slice
            {
                slice.ReferenceFrame = 0xFF;
                rbsp.ReadU((int)(_h265Log2MaxPicOrderCntLsbMinus4 + 4)); // slice_pic_order_cnt_lsb
                if (rbsp.ReadU(1) == 0) // short_term_ref_pic_set_sps_flag
                {
                    uint numNegativePics = rbsp.ReadUE();
                    if (numNegativePics > 16)
                    {
                        _logger?.LogWarning("parse_slice_h265: Unexpected num_negative_pics {Count}", numNegativePics);
                        return false;
                    }
                    rbsp.ReadUE(); // num_positive_pics
                    for (uint i = 0; i < numNegativePics; i++)
                    {
                        rbsp.ReadUE(); // delta_poc_s0_minus1[i]
                        if (rbsp.ReadU(1) != 0) // used_by_curr_pic_s0_flag[i]
                        {
                            slice.ReferenceFrame = i;
                            break;
                        }
                    }
                }
                if (slice.ReferenceFrame == 0xFF)
                    _logger?.LogDebug("parse_slice_h265: No ref frame found");
            }

            return true;
        }

        /// <summary>
        /// 修改 H.265 slice 中的参考帧索引
        /// 参考 chiaki 的 slice_set_reference_frame_h265 实现
        /// </summary>
        public bool SetReferenceFrame(byte[] frameData, uint newReferenceFrame, out byte[] modifiedFrameData)
        {
            modifiedFrameData = frameData;
            
            if (_codec != "hevc" && _codec != "h265")
            {
                _logger?.LogWarning("SetReferenceFrame 仅支持 H.265");
                return false;
            }

            try
            {
                return SetReferenceFrameH265(frameData, newReferenceFrame, out modifiedFrameData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "修改参考帧失败");
                return false;
            }
        }

        /// <summary>
        /// 修改 H.265 参考帧
        /// </summary>
        private bool SetReferenceFrameH265(byte[] frameData, uint referenceFrame, out byte[] modifiedFrameData)
        {
            modifiedFrameData = new byte[frameData.Length];
            Array.Copy(frameData, modifiedFrameData, frameData.Length);

            var vlc = new VlcParser(modifiedFrameData, 0, modifiedFrameData.Length, null);
            if (!SkipStartCode(vlc))
            {
                _logger?.LogWarning("slice_set_reference_frame_h265: No startcode found");
                return false;
            }

            vlc.EatBits(1); // forbidden_zero_bit
            uint nalUnitType = vlc.GetUimsbf(6);
            vlc.EatBits(6); // nuh_layer_id
            vlc.EatBits(3); // nuh_temporal_id_plus1

            if (nalUnitType != 1)
            {
                _logger?.LogWarning("slice_set_reference_frame_h265: Unexpected NAL unit type {NalType}", nalUnitType);
                return false;
            }

            var rbsp = new RbspParser(vlc, 0xFFFFFFFF, null);
            uint firstSliceSegmentInPicFlag = rbsp.ReadU(1);

            rbsp.ReadUE(); // slice_pic_parameter_set_id
            if (firstSliceSegmentInPicFlag == 0)
                rbsp.ReadUE(); // slice_segment_address

            if (rbsp.ReadUE() != 1) // slice_type
            {
                _logger?.LogWarning("slice_set_reference_frame_h265: Not P slice");
                return false;
            }

            rbsp.ReadU((int)(_h265Log2MaxPicOrderCntLsbMinus4 + 4)); // slice_pic_order_cnt_lsb
            if (rbsp.ReadU(1) == 0) // short_term_ref_pic_set_sps_flag
            {
                uint numNegativePics = rbsp.ReadUE();
                if (numNegativePics > 16)
                {
                    _logger?.LogWarning("slice_set_reference_frame_h265: Unexpected num_negative_pics {Count}", numNegativePics);
                    return false;
                }
                rbsp.ReadUE(); // num_positive_pics
                
                // 获取底层 VLC 解析器以修改 bitstream
                var nal = rbsp.GetNal();
                
                for (uint i = 0; i < numNegativePics; i++)
                {
                    rbsp.ReadUE(); // delta_poc_s0_minus1[i]
                    
                    // 修改 used_by_curr_pic_s0_flag[i] 位
                    // 参考 chiaki 的实现：直接修改原始数据缓冲区中的位
                    // chiaki: d = (uint32_t*)rbsp.nal.data
                    //         hi = ntohl(d[-2]), lo = ntohl(d[-1])
                    //         buffer = lo | (hi << 32)
                    //         mask = 1 << (64 - 1 - (64 - (32 - rbsp.nal.invalid_bits)))
                    
                    int invalidBits = nal.GetInvalidBits();
                    int dataOffset = nal.GetDataPointerOffset();
                    
                    // 读取当前缓冲区中的两个 32 位字（data[-2] 和 data[-1]）
                    // 注意：data[-2] 和 data[-1] 是相对于当前数据指针的前两个 32 位字
                    if (dataOffset >= 8)
                    {
                        // 读取 data[-2] 和 data[-1]（大端序）
                        uint hi = (uint)(modifiedFrameData[dataOffset - 8] << 24) |
                                  (uint)(modifiedFrameData[dataOffset - 7] << 16) |
                                  (uint)(modifiedFrameData[dataOffset - 6] << 8) |
                                  (uint)(modifiedFrameData[dataOffset - 5]);
                        uint lo = (uint)(modifiedFrameData[dataOffset - 4] << 24) |
                                  (uint)(modifiedFrameData[dataOffset - 3] << 16) |
                                  (uint)(modifiedFrameData[dataOffset - 2] << 8) |
                                  (uint)(modifiedFrameData[dataOffset - 1]);
                        
                        // 转换为小端序（如果系统是小端序）
                        if (BitConverter.IsLittleEndian)
                        {
                            hi = (hi >> 24) | ((hi >> 8) & 0xFF00) | ((hi << 8) & 0xFF0000) | (hi << 24);
                            lo = (lo >> 24) | ((lo >> 8) & 0xFF00) | ((lo << 8) & 0xFF0000) | (lo << 24);
                        }
                        
                        ulong buffer = lo | ((ulong)hi << 32);
                        
                        // 计算要修改的位的位置
                        // chiaki: mask = (uint64_t)1 << (64 - 1 - (64 - (32 - rbsp.nal.invalid_bits)))
                        // 简化：mask = 1 << (64 - 1 - 64 + 32 - invalid_bits) = 1 << (31 - invalid_bits)
                        ulong mask = 1UL << (63 - (64 - (32 - invalidBits)));
                        
                        if (i == referenceFrame)
                        {
                            buffer |= mask; // set 1
                        }
                        else
                        {
                            buffer &= ~mask; // set 0
                        }
                        
                        // 将修改后的值写回原始数据（转换为大端序）
                        hi = (uint)(buffer >> 32);
                        lo = (uint)(buffer & 0xFFFFFFFF);
                        
                        if (BitConverter.IsLittleEndian)
                        {
                            hi = (hi >> 24) | ((hi >> 8) & 0xFF00) | ((hi << 8) & 0xFF0000) | (hi << 24);
                            lo = (lo >> 24) | ((lo >> 8) & 0xFF00) | ((lo << 8) & 0xFF0000) | (lo << 24);
                        }
                        
                        modifiedFrameData[dataOffset - 8] = (byte)(hi >> 24);
                        modifiedFrameData[dataOffset - 7] = (byte)(hi >> 16);
                        modifiedFrameData[dataOffset - 6] = (byte)(hi >> 8);
                        modifiedFrameData[dataOffset - 5] = (byte)hi;
                        modifiedFrameData[dataOffset - 4] = (byte)(lo >> 24);
                        modifiedFrameData[dataOffset - 3] = (byte)(lo >> 16);
                        modifiedFrameData[dataOffset - 2] = (byte)(lo >> 8);
                        modifiedFrameData[dataOffset - 1] = (byte)lo;
                        
                        if (i == referenceFrame)
                            return true;
                    }
                    
                    rbsp.ReadU(1); // used_by_curr_pic_s0_flag[i]
                }
            }

            return false;
        }
    }
}
