namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 流统计器 - 跟踪帧数和字节数，计算实际码率
    /// </summary>
    public class StreamStats
    {
        private ulong _frames = 0;
        private ulong _bytes = 0;
        private readonly object _lock = new object();

        /// <summary>
        /// 总帧数
        /// </summary>
        public ulong Frames
        {
            get
            {
                lock (_lock)
                {
                    return _frames;
                }
            }
        }

        /// <summary>
        /// 总字节数
        /// </summary>
        public ulong Bytes
        {
            get
            {
                lock (_lock)
                {
                    return _bytes;
                }
            }
        }

        /// <summary>
        /// 重置统计信息
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _frames = 0;
                _bytes = 0;
            }
        }

        /// <summary>
        /// 记录一帧
        /// </summary>
        /// <param name="size">帧大小（字节）</param>
        public void RecordFrame(ulong size)
        {
            lock (_lock)
            {
                _frames++;
                _bytes += size;
            }
        }

        /// <summary>
        /// 计算码率（比特/秒）
        /// 公式: (bytes * 8 * framerate) / frames
        /// </summary>
        /// <param name="framerate">帧率（fps）</param>
        /// <returns>码率（bps），如果 frames == 0 则返回 0</returns>
        public ulong CalculateBitrate(ulong framerate)
        {
            lock (_lock)
            {
                if (_frames == 0)
                    return 0;
                
                return (_bytes * 8 * framerate) / _frames;
            }
        }

        /// <summary>
        /// 获取码率（Mbps）
        /// </summary>
        /// <param name="framerate">帧率（fps）</param>
        /// <returns>码率（Mbps）</returns>
        public double GetBitrateMbps(ulong framerate)
        {
            ulong bps = CalculateBitrate(framerate);
            return bps / 1000000.0;
        }

        /// <summary>
        /// 获取当前统计的快照（线程安全）
        /// </summary>
        public (ulong frames, ulong bytes) GetSnapshot()
        {
            lock (_lock)
            {
                return (_frames, _bytes);
            }
        }

        /// <summary>
        /// 获取并重置统计信息（原子操作）
        /// </summary>
        public (ulong frames, ulong bytes) GetAndReset()
        {
            lock (_lock)
            {
                var snapshot = (_frames, _bytes);
                _frames = 0;
                _bytes = 0;
                return snapshot;
            }
        }
    }
}



