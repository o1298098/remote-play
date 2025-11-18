using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace RemotePlay.Services.Streaming.AV.Bitstream
{
    /// <summary>
    /// 参考帧管理器 - 管理已成功解码的参考帧
    /// </summary>
    public class ReferenceFrameManager
    {
        private readonly ILogger<ReferenceFrameManager>? _logger;
        private readonly int[] _referenceFrames; // -1 表示空槽位
        private const int MAX_REFERENCE_FRAMES = 16;
        private readonly object _lock = new();

        public ReferenceFrameManager(ILogger<ReferenceFrameManager>? logger = null)
        {
            _logger = logger;
            _referenceFrames = new int[MAX_REFERENCE_FRAMES];
            Reset();
        }

        /// <summary>
        /// 重置参考帧列表
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                for (int i = 0; i < MAX_REFERENCE_FRAMES; i++)
                {
                    _referenceFrames[i] = -1;
                }
            }
        }

        /// <summary>
        /// 添加参考帧
        /// </summary>
        public void AddReferenceFrame(int frameIndex)
        {
            lock (_lock)
            {
                // 如果第一个槽位不为空，向右移动
                if (_referenceFrames[0] != -1)
                {
                    Array.Copy(_referenceFrames, 0, _referenceFrames, 1, MAX_REFERENCE_FRAMES - 1);
                    _referenceFrames[0] = frameIndex;
                    return;
                }

                // 从后往前找第一个空槽位
                for (int i = MAX_REFERENCE_FRAMES - 1; i >= 0; i--)
                {
                    if (_referenceFrames[i] == -1)
                    {
                        _referenceFrames[i] = frameIndex;
                        return;
                    }
                }

                // 如果所有槽位都满了，替换最后一个
                _referenceFrames[MAX_REFERENCE_FRAMES - 1] = frameIndex;
            }
        }

        /// <summary>
        /// 检查是否有指定的参考帧
        /// </summary>
        public bool HasReferenceFrame(int frameIndex)
        {
            lock (_lock)
            {
                for (int i = 0; i < MAX_REFERENCE_FRAMES; i++)
                {
                    if (_referenceFrames[i] == frameIndex)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 查找可用的参考帧（从指定索引开始向后查找）
        /// 返回找到的参考帧索引，如果未找到返回 -1
        /// </summary>
        public int FindAvailableReferenceFrame(int currentFrameIndex, uint startReferenceFrame)
        {
            lock (_lock)
            {
                // 从 startReferenceFrame+1 到 15 查找可用的参考帧
                for (uint i = startReferenceFrame + 1; i < MAX_REFERENCE_FRAMES; i++)
                {
                    int refFrameIndex = currentFrameIndex - (int)i - 1;
                    if (HasReferenceFrame(refFrameIndex))
                    {
                        return (int)i;
                    }
                }
                return -1;
            }
        }

        /// <summary>
        /// 获取当前参考帧列表（用于调试）
        /// </summary>
        public int[] GetReferenceFrames()
        {
            lock (_lock)
            {
                var result = new int[MAX_REFERENCE_FRAMES];
                Array.Copy(_referenceFrames, result, MAX_REFERENCE_FRAMES);
                return result;
            }
        }
    }
}

