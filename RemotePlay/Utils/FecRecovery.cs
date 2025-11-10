using Microsoft.Extensions.Logging;

namespace RemotePlay.Utils
{
    /// <summary>
    /// FEC 恢复工具 - 基于 pyjerasure 逻辑实现
    /// </summary>
    public static class FecRecovery
    {
        /// <summary>
        /// 计算对齐大小（模拟 pyjerasure.align_size）
        /// 采用 8 字节对齐（w=8）
        /// </summary>
        public static int AlignSize(int maxSize)
        {
            // pyjerasure 默认 w=8，按 8 字节对齐
            return (maxSize + 7) & ~7;
        }

        /// <summary>
        /// 尝试恢复缺失的包
        /// </summary>
        /// <param name="packets">包列表（包含占位符的空包）</param>
        /// <param name="missingIndices">缺失的包索引列表</param>
        /// <param name="unitsSrc">源包数量</param>
        /// <param name="unitsFec">FEC 包数量</param>
        /// <param name="logger">日志记录器</param>
        /// <returns>恢复是否成功</returns>
        public static bool TryRecover(
            List<byte[]> packets,
            List<int> missingIndices,
            int unitsSrc,
            int unitsFec,
            ILogger logger)
        {
            if (missingIndices == null || missingIndices.Count == 0)
                return false;

            if (missingIndices.Count > unitsFec)
            {
                logger.LogWarning("⚠️ FEC 不足：缺失 {Missing} 个包，但只有 {Fec} 个 FEC 包",
                    missingIndices.Count, unitsFec);
                return false;
            }

            int maxSize = 0;
            for (int i = 0; i < packets.Count; i++)
            {
                if (packets[i] != null && packets[i].Length > 0)
                {
                    maxSize = Math.Max(maxSize, packets[i].Length);
                }
            }

            if (maxSize == 0)
            {
                logger.LogWarning("⚠️ 所有包都为空，无法进行 FEC 恢复");
                return false;
            }

            int alignedSize = AlignSize(maxSize);

            int totalUnits = unitsSrc + unitsFec;
            var shards = new byte[totalUnits][];
            var shardPresent = new bool[totalUnits];

            // 将所有包填充到对齐大小
            for (int i = 0; i < totalUnits && i < packets.Count; i++)
            {
                var pkt = packets[i];
                if (pkt != null && pkt.Length > 0)
                {
                    // 填充到对齐大小
                    shards[i] = new byte[alignedSize];
                    Buffer.BlockCopy(pkt, 0, shards[i], 0, Math.Min(pkt.Length, alignedSize));
                    // 剩余部分已经是 0（new byte 初始化）
                    shardPresent[i] = true;
                }
                else
                {
                    shards[i] = new byte[alignedSize];
                    shardPresent[i] = false;
                }
            }

            // 补齐所有分片
            for (int i = 0; i < totalUnits; i++)
            {
                if (shards[i] == null)
                {
                    shards[i] = new byte[alignedSize];
                    shardPresent[i] = false;
                }
            }

            try
            {
                logger.LogDebug("🧩 尝试 FEC 解码：缺失 {Missing} 个包，对齐大小 {Size}，源包 {Src}，FEC {Fec}",
                    missingIndices.Count, alignedSize, unitsSrc, unitsFec);

                var codec = ReedSolomon.NET.ReedSolomon.Create(unitsSrc, unitsFec);
                codec.DecodeMissing(shards, shardPresent, 0, alignedSize);

                // 恢复成功后，将恢复的数据写回到 packets[index]，并去掉尾部零
                // for index in missing:
                //     packets[index] = restored[size * index : size * (index + 1)].rstrip(b"\x00")
                // 注意：ReedSolomon.NET 的 DecodeMissing 会直接修改 shards 数组中缺失的分片
                bool anyRestored = false;
                foreach (int index in missingIndices)
                {
                    if (index >= 0 && index < shards.Length && index < packets.Count)
                    {
                        // DecodeMissing 会将缺失的分片恢复到 shards[index]
                        var restored = shards[index];
                        
                        // rstrip(b"\x00")：从尾部去掉零
                        int actualLength = restored.Length;
                        while (actualLength > 0 && restored[actualLength - 1] == 0)
                        {
                            actualLength--;
                        }

                        if (actualLength > 0)
                        {
                            // 写回到 packets[index]
                            var trimmed = new byte[actualLength];
                            Buffer.BlockCopy(restored, 0, trimmed, 0, actualLength);
                            packets[index] = trimmed;
                            anyRestored = true;
                        }
                    }
                }

                if (anyRestored)
                {
                    logger.LogDebug("✅ FEC 成功恢复 {Count} 个缺失包", missingIndices.Count);
                    return true;
                }
                else
                {
                    logger.LogWarning("⚠️ FEC 解码未恢复任何包");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "❌ FEC 恢复失败");
                return false;
            }
        }
    }
}