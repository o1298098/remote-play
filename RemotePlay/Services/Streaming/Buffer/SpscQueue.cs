using System;
using System.Threading;

namespace RemotePlay.Services.Streaming.Buffer
{
    /// <summary>
    /// 单生产者单消费者（SPSC）无锁队列
    /// 使用 Interlocked 操作实现高性能的队列操作
    /// 容量必须是 2 的幂次方
    /// </summary>
    public sealed class SpscQueue<T> where T : class
    {
        private readonly T?[] _buffer;
        private readonly int _mask;
        private long _head; // 下一个读取索引（消费者）
        private long _tail; // 下一个写入索引（生产者）

        public int Capacity { get; }

        /// <summary>
        /// 创建 SPSC 队列
        /// </summary>
        /// <param name="capacityPowerOfTwo">容量（必须是 2 的幂次方，且 >= 2）</param>
        public SpscQueue(int capacityPowerOfTwo)
        {
            if (capacityPowerOfTwo < 2 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
                throw new ArgumentException("capacityPowerOfTwo must be a power of two >= 2", nameof(capacityPowerOfTwo));

            Capacity = capacityPowerOfTwo;
            _buffer = new T[Capacity];
            _mask = Capacity - 1;
            _head = 0;
            _tail = 0;
        }

        /// <summary>
        /// 尝试入队（生产者调用）
        /// </summary>
        public bool TryEnqueue(T item)
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            var tail = Interlocked.Read(ref _tail);
            var head = Interlocked.Read(ref _head);

            // 检查队列是否已满
            if (tail - head >= Capacity)
                return false; // 队列已满

            var idx = (int)(tail & _mask);
            _buffer[idx] = item;

            // 使用内存屏障确保写入在更新 tail 之前完成
            Interlocked.Exchange(ref _tail, tail + 1);
            return true;
        }

        /// <summary>
        /// 尝试出队（消费者调用）
        /// </summary>
        public bool TryDequeue(out T? item)
        {
            var head = Interlocked.Read(ref _head);
            var tail = Interlocked.Read(ref _tail);

            if (tail == head)
            {
                item = null;
                return false; // 队列为空
            }

            var idx = (int)(head & _mask);
            item = _buffer[idx];
            _buffer[idx] = null; // 允许 GC 回收

            // 使用内存屏障确保读取在更新 head 之前完成
            Interlocked.Exchange(ref _head, head + 1);
            return true;
        }

        /// <summary>
        /// 获取队列中的元素数量（近似值，因为是无锁的）
        /// </summary>
        public int Count
        {
            get
            {
                var tail = Interlocked.Read(ref _tail);
                var head = Interlocked.Read(ref _head);
                var c = (int)(tail - head);
                return c < 0 ? 0 : Math.Min(c, Capacity);
            }
        }

        /// <summary>
        /// 清空队列
        /// </summary>
        public void Clear()
        {
            while (TryDequeue(out _)) { }
        }

        /// <summary>
        /// 检查队列是否为空
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                var tail = Interlocked.Read(ref _tail);
                var head = Interlocked.Read(ref _head);
                return tail == head;
            }
        }
    }
}

