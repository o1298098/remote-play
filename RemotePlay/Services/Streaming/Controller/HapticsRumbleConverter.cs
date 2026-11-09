using RemotePlay.Services.Streaming.AV;
using System.Buffers.Binary;

namespace RemotePlay.Services.Streaming.Controller
{
    /// <summary>
    /// 把 PS5 下发的 DualSense haptics 音频（16 位立体声 PCM，每帧约 10ms）换算成普通双马达震动强度。
    /// 开启 DualSense 特性后，支持 haptics 的游戏不再发普通震动包，浏览器 Gamepad API 只支持 dual-rumble，
    /// 因此需要这层换算（算法同 chiaki-ng 的 rumble haptics）
    /// </summary>
    public sealed class HapticsRumbleConverter : IDisposable
    {
        // 每次输出震动合并的 haptics 帧数，及对应的输出间隔
        private const int PACKETS_PER_RUMBLE = 3;
        private const int RUMBLE_INTERVAL_MS = PACKETS_PER_RUMBLE * 10;
        private const uint MIN_STRENGTH = 100;
        // 部分手柄震动强度低于 9 位时不会转动
        private const uint MIN_MOTOR_STRENGTH = 1 << 9;
        private const int MAX_QUEUED_FRAMES = 20;

        private readonly ILogger<HapticsRumbleConverter> _logger;
        private readonly Action<byte, byte> _onRumble;
        private readonly object _lock = new();
        private readonly Queue<ushort> _strengths = new();
        private ushort _frameIndexPrev;
        private bool _frameIndexStartup = true;
        private bool _rumbleOn;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <param name="onRumble">输出震动（左、右电机强度 0-255）</param>
        public HapticsRumbleConverter(ILogger<HapticsRumbleConverter> logger, Action<byte, byte> onRumble)
        {
            _logger = logger;
            _onRumble = onRumble;
        }

        public void Start(CancellationToken cancellationToken)
        {
            if (_loop != null)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => RumbleLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 处理一个 haptics AV 包（data 为已解密数据），按音频单元拆成 haptics 帧
        /// </summary>
        public void ProcessPacket(AVPacket packet, byte[] data)
        {
            int sourceUnits = packet.UnitsSrc;
            int fecUnits = packet.UnitsFec;
            int unitSize = packet.AudioUnitSize;

            if (unitSize <= 0 || data.Length == 0 || sourceUnits + fecUnits != packet.UnitsTotal)
            {
                return;
            }

            if (data.Length != unitSize * packet.UnitsTotal)
            {
                _logger.LogDebug("Haptics packet size mismatch {Size} vs {Expected}", data.Length, unitSize * packet.UnitsTotal);
                return;
            }

            lock (_lock)
            {
                if (packet.FrameIndex > (1 << 15))
                {
                    _frameIndexStartup = false;
                }

                for (int i = 0; i < sourceUnits + fecUnits; i++)
                {
                    ushort frameIndex;
                    if (i < sourceUnits)
                    {
                        frameIndex = (ushort)(packet.FrameIndex + i);
                    }
                    else
                    {
                        int fecIndex = i - sourceUnits;
                        if (_frameIndexStartup && packet.FrameIndex + fecIndex < fecUnits + 1)
                        {
                            continue;
                        }
                        frameIndex = (ushort)(packet.FrameIndex - fecUnits + fecIndex);
                    }

                    if ((short)(frameIndex - _frameIndexPrev) <= 0)
                    {
                        continue;
                    }

                    _frameIndexPrev = frameIndex;
                    PushFrame_NoLock(data.AsSpan(unitSize * i, unitSize));
                }
            }
        }

        private void PushFrame_NoLock(ReadOnlySpan<byte> frame)
        {
            const int sampleSize = 2 * sizeof(short); // 立体声
            if (frame.Length == 0 || frame.Length % sampleSize != 0)
            {
                return;
            }

            int count = frame.Length / sampleSize;
            ulong sumLeft = 0;
            ulong sumRight = 0;
            for (int i = 0; i < count; i++)
            {
                var sample = frame.Slice(i * sampleSize, sampleSize);
                sumLeft += (ulong)Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(sample)) * 2;
                sumRight += (ulong)Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(2))) * 2;
            }

            uint left = (uint)(sumLeft / (ulong)count);
            uint right = (uint)(sumRight / (ulong)count);
            left = left > MIN_STRENGTH ? Math.Min(left, ushort.MaxValue) : 0;
            right = right > MIN_STRENGTH ? Math.Min(right, ushort.MaxValue) : 0;
            if (left == 0 && right == 0)
            {
                return;
            }

            left = left > 0 && left < MIN_MOTOR_STRENGTH ? MIN_MOTOR_STRENGTH : left;
            right = right > 0 && right < MIN_MOTOR_STRENGTH ? MIN_MOTOR_STRENGTH : right;

            if (_strengths.Count >= MAX_QUEUED_FRAMES)
            {
                _strengths.Dequeue();
            }
            _strengths.Enqueue((ushort)Math.Max(left, right));
        }

        private async Task RumbleLoopAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(RUMBLE_INTERVAL_MS));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    uint sum = 0;
                    lock (_lock)
                    {
                        for (int i = 0; i < PACKETS_PER_RUMBLE && _strengths.Count > 0; i++)
                        {
                            sum += _strengths.Dequeue();
                        }
                    }

                    uint strength = sum / PACKETS_PER_RUMBLE;
                    bool emit = strength > 0 || _rumbleOn;
                    _rumbleOn = strength > 0;
                    if (emit)
                    {
                        byte value = (byte)(strength >> 8);
                        _onRumble(value, value);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Haptics rumble loop error");
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
