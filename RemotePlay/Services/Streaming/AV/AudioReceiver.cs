

using RemotePlay.Services.Streaming.Protocol;
using System;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 音频接收器
    /// 每个 unit 作为独立的音频帧处理，而不是组装多个 unit
    /// </summary>
    public class AudioReceiver
    {
        private readonly ILogger<AudioReceiver>? _logger;
        private ushort _frameIndexPrev = 0;
        private int _frameReceivedCount = 0; // 启动期计数器，替代依赖序列号大小的判断
        private Action<int>? _onFrameLossCallback; // ✅ 帧丢失回调，参数为丢失的帧数

        // ✅ 帧丢失检测阈值：当帧索引跳跃超过此值时，认为发生了帧丢失
        private const int MAX_FRAME_GAP = 5; // 允许最多丢失 5 帧，超过则重置解码器
        private const int STARTUP_FRAME_COUNT = 10; // 启动期：连续接收10帧后结束启动期

        public AudioReceiver(ILogger<AudioReceiver>? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 设置帧丢失回调（当检测到帧丢失时调用，参数为丢失的帧数）
        /// </summary>
        public void SetFrameLossCallback(Action<int>? callback)
        {
            _onFrameLossCallback = callback;
        }

        public void SetHeader(byte[] header)
        {
            // Header 通过 stream_info 单独处理，这里不需要保存
        }

        /// <summary>
        /// 处理音频 AV 包
        /// 注意：一个 AV packet 包含多个 unit，每个 unit 作为独立的音频帧
        /// </summary>
        public void ProcessPacket(AVPacket packet, byte[] decryptedData, Action<byte[]> onFrameReady)
        {
            if (packet.Type != HeaderType.AUDIO)
                return;

            int sourceUnitsCount = packet.UnitsSrc;
            int fecUnitsCount = packet.UnitsFec;
            int unitSize = packet.AudioUnitSize;

            if (decryptedData.Length == 0)
            {
                _logger?.LogError("Audio AV Packet is empty");
                return;
            }

            if (sourceUnitsCount + fecUnitsCount != packet.UnitsTotal)
            {
                _logger?.LogError("Source Units + FEC Units != Total Units in Audio AV Packet: {Src}+{Fec}!={Total}",
                    sourceUnitsCount, fecUnitsCount, packet.UnitsTotal);
                return;
            }

            // 检查数据大小是否匹配
            int expectedDataSize = unitSize * packet.UnitsTotal;
            if (decryptedData.Length != expectedDataSize)
            {
                _logger?.LogError("Audio AV Packet size mismatch: expected {Expected}, got {Actual}",
                    expectedDataSize, decryptedData.Length);
                return;
            }

            // ⚠️ 优化：使用计数器而不是序列号大小来判断启动期
            bool isStartup = _frameReceivedCount < STARTUP_FRAME_COUNT;

            // 遍历所有 unit（source + fec），每个 unit 作为独立的音频帧
            for (int i = 0; i < sourceUnitsCount + fecUnitsCount; i++)
            {
                ushort frameIndex;
                if (i < sourceUnitsCount)
                {
                    // Source unit: frame_index = packet.frame_index + i
                    frameIndex = (ushort)((packet.FrameIndex + i) & 0xFFFF);
                }
                else
                {
                    // FEC unit
                    int fecIndex = i - sourceUnitsCount;

                    // 启动期：忽略重复的帧
                    if (isStartup && packet.FrameIndex + fecIndex < fecUnitsCount + 1)
                        continue;

                    // FEC unit: frame_index = packet.frame_index - fec_units_count + fec_index
                    // ⚠️ 关键修复：使用 & 0xFFFF 防止负数导致的大序列号
                    frameIndex = (ushort)((packet.FrameIndex - fecUnitsCount + fecIndex) & 0xFFFF);
                }

                // 提取当前 unit 的数据
                int unitOffset = unitSize * i;
                if (unitOffset + unitSize > decryptedData.Length)
                {
                    _logger?.LogError("Audio unit {Unit} offset out of bounds: offset={Offset}, size={Size}, dataLen={DataLen}",
                        i, unitOffset, unitSize, decryptedData.Length);
                    continue;
                }

                byte[] unitData = new byte[unitSize];
                System.Buffer.BlockCopy(decryptedData, unitOffset, unitData, 0, unitSize);

                // 发送单个 unit 作为音频帧
                ProcessSingleUnit(frameIndex, unitData, onFrameReady);
            }
        }

        /// <summary>
        /// 处理单个音频 unit
        /// </summary>
        private void ProcessSingleUnit(ushort frameIndex, byte[] unitData, Action<byte[]> onFrameReady)
        {
            // 检查 frame_index 是否大于上一个
            if (!IsFrameIndexGreater(frameIndex, _frameIndexPrev))
                return; // 跳过旧的或重复的帧

            // ✅ 检测帧丢失：如果帧索引跳跃过大，说明发生了丢包
            // ⚠️ 关键修复：检测回绕，如果是回绕则不应该触发帧丢失回调
            bool isWrapAround = IsWrapAround(_frameIndexPrev, frameIndex);
            int frameGap = CalculateFrameGap(frameIndex, _frameIndexPrev);
            
            // 只有在非回绕情况下才检测帧丢失
            if (!isWrapAround && frameGap > MAX_FRAME_GAP)
            {
                _logger?.LogWarning("⚠️ 检测到音频帧丢失：从 {Prev} 跳到 {Current}，丢失 {Gap} 帧，将重置解码器",
                    _frameIndexPrev, frameIndex, frameGap);
                
                // 通知上层重置解码器，传递丢失的帧数
                _onFrameLossCallback?.Invoke(frameGap);
            }
            else if (isWrapAround)
            {
                // 回绕是正常情况，记录信息但不触发帧丢失回调
                _logger?.LogInformation("ℹ️ 音频序列号回绕：从 {Prev} 回绕到 {Current}",
                    _frameIndexPrev, frameIndex);
            }

            _frameIndexPrev = frameIndex;

            // ⚠️ 优化：更新启动期计数器
            _frameReceivedCount++;

            // 直接发送 unit 数据作为音频帧
            onFrameReady(unitData);
        }

        /// <summary>
        /// 检查是否是序列号回绕
        /// </summary>
        private static bool IsWrapAround(ushort prev, ushort current)
        {
            // ⚠️ 优化：统一回绕判断逻辑
            // 当上一帧大于 0x8000 (32768) 且当前帧小于 0x8000 时，认为是回绕
            // 例如：prev=35976, current=2145，这是回绕（从35976增长到65535，然后回绕到0，再到2145）
            return prev > 0x8000u && current < 0x8000u;
        }

        /// <summary>
        /// 计算帧索引之间的差距（不考虑回绕，因为回绕已经在IsWrapAround中处理）
        /// </summary>
        private static int CalculateFrameGap(ushort current, ushort prev)
        {
            // ⚠️ 优化：只计算正常增长的差距，回绕情况已经在ProcessSingleUnit中被过滤
            // 正常情况下，diff 应该 < 0x8000
            // 如果 diff >= 0x8000，说明是向后回绕（current在prev之前），这种情况应该已经被IsFrameIndexGreater过滤
            int diff = (current - prev) & 0xFFFF;
            return diff;
        }

        /// <summary>
        /// 检查 frame_index 是否大于 prev（考虑回绕）
        /// </summary>
        private static bool IsFrameIndexGreater(ushort frameIndex, ushort prev)
        {
            // 16位序列号比较，考虑回绕
            int diff = (frameIndex - prev) & 0xFFFF;
            
            // 如果diff == 0，是同一个帧
            if (diff == 0)
                return false;
            // ⚠️ 优化：使用统一的回绕检测逻辑
            if (IsWrapAround(prev, frameIndex))
            {
                // 这是回绕情况，frameIndex是回绕后的新序列号
                return true;
            }
            
            // 正常情况：diff > 0 且 diff < 0x8000 表示frameIndex在prev之后
            return diff > 0 && diff < 0x8000;
        }
    }
}

