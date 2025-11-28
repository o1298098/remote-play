using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 视频队列管理器 - 无锁实现
    /// 使用 ConcurrentQueue 避免死锁和锁竞争
    /// </summary>
    internal class VideoQueueManager
    {
        private readonly ConcurrentQueue<VideoFrame> _idrQueue = new();
        private readonly ConcurrentQueue<VideoFrame> _normalQueue = new();
        
        private const int MAX_QUEUE_SIZE = 20;
        private const int MAX_IDR_QUEUE_SIZE = 10; // IDR队列通常较小

        /// <summary>
        /// 入队IDR帧（优先队列）
        /// </summary>
        public bool TryEnqueueIdr(VideoFrame frame)
        {
            if (frame == null) return false;
            
            _idrQueue.Enqueue(frame);
            while (_idrQueue.Count > MAX_IDR_QUEUE_SIZE)
            {
                _idrQueue.TryDequeue(out _);
            }
            
            return true;
        }

        /// <summary>
        /// 入队普通帧
        /// </summary>
        public bool TryEnqueueNormal(VideoFrame frame)
        {
            if (frame == null) return false;
            
            _normalQueue.Enqueue(frame);
            while (TotalCount > MAX_QUEUE_SIZE && _normalQueue.Count > 0)
            {
                _normalQueue.TryDequeue(out _);
            }
            
            return true;
        }

        /// <summary>
        /// 批量出队（优先IDR帧）
        /// </summary>
        public int TryDequeueBatch(List<VideoFrame> output, int maxCount)
        {
            if (output == null) return 0;
            
            int dequeued = 0;
            
            while (dequeued < maxCount && _idrQueue.TryDequeue(out var idrFrame))
            {
                output.Add(idrFrame);
                dequeued++;
            }
            
            while (dequeued < maxCount && _normalQueue.TryDequeue(out var normalFrame))
            {
                output.Add(normalFrame);
                dequeued++;
            }
            
            return dequeued;
        }

        /// <summary>
        /// 清除所有队列
        /// </summary>
        public void Clear()
        {
            while (_idrQueue.TryDequeue(out _)) { }
            while (_normalQueue.TryDequeue(out _)) { }
        }

        /// <summary>
        /// 清除部分旧帧（保留最近的）
        /// </summary>
        public int ClearOldFrames(int framesToKeep)
        {
            int cleared = 0;
            
            // 只清除普通帧队列，保留IDR队列
            int normalCount = _normalQueue.Count;
            int toClear = Math.Max(0, normalCount - framesToKeep);
            
            for (int i = 0; i < toClear; i++)
            {
                if (_normalQueue.TryDequeue(out _))
                {
                    cleared++;
                }
            }
            
            return cleared;
        }

        /// <summary>
        /// 总队列大小
        /// </summary>
        public int TotalCount => _idrQueue.Count + _normalQueue.Count;

        /// <summary>
        /// IDR队列大小
        /// </summary>
        public int IdrQueueCount => _idrQueue.Count;

        /// <summary>
        /// 普通帧队列大小
        /// </summary>
        public int NormalQueueCount => _normalQueue.Count;
    }
}

