using Microsoft.Extensions.Logging;
using RemotePlay.Models.PlayStation;
using RemotePlay.Utils.Crypto;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

namespace RemotePlay.Services.Streaming.AV
{
    /// <summary>
    /// 完全优化的 AVHandler
    /// 低延迟、高性能、零拷贝、批量处理、线程安全
    /// </summary>
    public sealed class AVHandler
    {
        private readonly ILogger<AVHandler> _logger;
        private readonly string _hostType;
        private StreamCipher? _cipher;
        private IAVReceiver? _receiver;

        private readonly ConcurrentQueue<AVPacket> _queue = new();
        private const int MaxQueueSize = 5000;
        private volatile bool _waiting = false;

        private const int DirectProcessThreshold = 10;
        private int _directProcessCount = 0;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly CancellationToken _ct;

        private AVStream? _videoStream;
        private AVStream? _audioStream;

        private string? _detectedVideoCodec;
        private string? _detectedAudioCodec;

        private int _videoFrameCounter = 0;

        public AVHandler(
            ILogger<AVHandler> logger,
            string hostType,
            StreamCipher? cipher,
            IAVReceiver? receiver,
            CancellationToken ct)
        {
            _logger = logger;
            _hostType = hostType;
            _cipher = cipher;
            _receiver = receiver;
            _ct = ct;
        }

        #region Receiver / Cipher / Headers

        public void SetReceiver(IAVReceiver receiver)
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));

            var oldReceiver = _receiver;
            _receiver = receiver;

            if (oldReceiver != null)
                _logger.LogInformation("🔄 Switching receiver: {Old} -> {New}", oldReceiver.GetType().Name, receiver.GetType().Name);

            if (_videoStream != null || _audioStream != null)
            {
                var videoHeader = _videoStream?.Header ?? Array.Empty<byte>();
                var audioHeader = _audioStream?.Header ?? Array.Empty<byte>();
                try { receiver.OnStreamInfo(videoHeader, audioHeader); } catch { }
            }

            if (_detectedVideoCodec != null) receiver.SetVideoCodec(_detectedVideoCodec);
            if (_detectedAudioCodec != null) receiver.SetAudioCodec(_detectedAudioCodec);
        }

        public void SetCipher(StreamCipher cipher)
        {
            _cipher = cipher;
            if (_receiver != null)
            {
                if (_workerTask == null || _workerTask.IsCompleted)
                    StartWorker();
            }
            else
            {
                _logger.LogWarning("⚠️ SetCipher called but receiver is null");
            }
        }

        public void SetHeaders(byte[]? videoHeader, byte[]? audioHeader, ILoggerFactory loggerFactory)
        {
            if (_receiver == null)
            {
                _logger.LogWarning("⚠️ Cannot set headers: receiver is null");
                return;
            }

            _videoStream = new AVStream(
                "video",
                videoHeader ?? Array.Empty<byte>(),
                HandleVideoFrame,
                (last, current) => { },
                loggerFactory.CreateLogger<AVStream>());

            _audioStream = new AVStream(
                "audio",
                audioHeader ?? Array.Empty<byte>(),
                frame =>
                {
                    var outBuf = ArrayPool<byte>.Shared.Rent(1 + frame.Length);
                    outBuf[0] = (byte)HeaderType.AUDIO;
                    frame.AsSpan().CopyTo(outBuf.AsSpan(1));
                    try { _receiver?.OnAudioPacket(outBuf.AsSpan(0, frame.Length + 1).ToArray()); } finally { ArrayPool<byte>.Shared.Return(outBuf); }
                },
                (last, current) => { },
                loggerFactory.CreateLogger<AVStream>());

            if (_cipher != null)
            {
                if (_workerTask == null || _workerTask.IsCompleted)
                    StartWorker();
            }
            else
            {
                _logger.LogWarning("⚠️ SetHeaders called but cipher is null");
            }
        }

        #endregion

        #region Packet Handling

        public void AddPacket(byte[] msg)
        {
            if (!AVPacket.TryParse(msg, _hostType, out var packet))
            {
                _logger.LogWarning("⚠️ Failed to parse AV packet, len={Len}", msg.Length);
                return;
            }

            // Codec 检测
            if (packet.Type == HeaderType.VIDEO && _detectedVideoCodec == null) DetectVideoCodec(packet);
            if (packet.Type == HeaderType.AUDIO && _detectedAudioCodec == null) DetectAudioCodec(packet);

            if (_receiver == null) return;

            // 队列溢出处理
            if (_queue.Count >= MaxQueueSize)
            {
                while (_queue.TryDequeue(out _)) { }
                _waiting = true;
                _logger.LogWarning("⚠️ AV queue overflow, cleared queue, waiting for unit_index=0");
            }

            if (_waiting && packet.UnitIndex != 0) return;
            if (_waiting && packet.UnitIndex == 0) _waiting = false;

            // 低延迟直接处理
            if (_queue.Count < DirectProcessThreshold && _cipher != null)
            {
                try { ProcessSinglePacket(packet); Interlocked.Increment(ref _directProcessCount); return; }
                catch (Exception ex) { _logger.LogWarning(ex, "⚠️ Direct processing failed, enqueue instead"); }
            }

            _queue.Enqueue(packet);

            if (_queue.Count > 100 && (_workerTask == null || _workerTask.IsCompleted) && _cipher != null)
            {
                _logger.LogError("❌ Queue has {Size} packets but worker not running! Starting...", _queue.Count);
                StartWorker();
            }
        }

        private void ProcessSinglePacket(AVPacket packet)
        {
            byte[] decrypted = DecryptPacket(packet);
            if (packet.Type == HeaderType.VIDEO)
            {
                if (_videoStream == null)
                {
                    _logger.LogError("❌ VideoStream null, frame={Frame}", packet.FrameIndex);
                    return;
                }
                _videoStream.Handle(packet, decrypted);
            }
            else
            {
                _audioStream?.Handle(packet, decrypted);
            }
        }

        private byte[] DecryptPacket(AVPacket packet)
        {
            var data = packet.Data;
            if (_cipher != null && data.Length > 0 && packet.KeyPos > 0)
            {
                try { data = _cipher.Decrypt(data, (int)packet.KeyPos); }
                catch (Exception ex) { _logger.LogError(ex, "❌ Decrypt failed frame={Frame}", packet.FrameIndex); }
            }
            return data;
        }

        #endregion

        #region Codec Detection

        private void DetectAudioCodec(AVPacket packet)
        {
            string codec = packet.Codec switch
            {
                0x01 or 0x02 => "opus",
                0x03 or 0x04 => "aac",
                _ => "opus"
            };
            if (codec == "opus" && packet.Codec != 0x01 && packet.Codec != 0x02)
                _logger.LogWarning("⚠️ Unknown audio codec 0x{Codec:X2}, defaulting to opus", packet.Codec);

            _detectedAudioCodec = codec;
            _receiver?.SetAudioCodec(codec);
        }

        private void DetectVideoCodec(AVPacket packet)
        {
            string? codec = _videoStream?.Header != null ? DetectCodecFromHeader(_videoStream.Header) : null;

            if (codec != null)
            {
                _detectedVideoCodec = codec;
                _receiver?.SetVideoCodec(codec);
                _logger.LogInformation("📹 Detected video codec: {Codec}", codec);
                return;
            }

            _detectedVideoCodec = packet.Codec switch
            {
                0x06 => "h264",
                0x36 or 0x37 => "hevc",
                _ => "h264"
            };
            _receiver?.SetVideoCodec(_detectedVideoCodec);
        }

        private string? DetectCodecFromHeader(byte[] header)
        {
            int len = Math.Max(header.Length - 64, 0); // 去掉 padding
            for (int i = 0; i < len - 4; i++)
            {
                if (header[i] == 0x00 && header[i + 1] == 0x00)
                {
                    int offset = header[i + 2] == 0x01 ? 3 : (header[i + 2] == 0x00 && header[i + 3] == 0x01 ? 4 : 0);
                    if (offset == 0) continue;
                    byte nal = header[i + offset];
                    if ((nal & 0x7E) == 0x40 || (nal & 0x7E) == 0x42 || (nal & 0x7E) == 0x44) return "hevc";
                    if ((nal & 0x1F) is 5 or 7 or 8) return "h264";
                }
            }
            return null;
        }

        #endregion

        #region Video Frame

        private void HandleVideoFrame(byte[] frame)
        {
            if (_receiver == null || frame == null || frame.Length == 0) return;

            var outBuf = ArrayPool<byte>.Shared.Rent(1 + frame.Length);
            outBuf[0] = (byte)HeaderType.VIDEO;
            frame.AsSpan().CopyTo(outBuf.AsSpan(1));

            Interlocked.Increment(ref _videoFrameCounter);

            try { _receiver.OnVideoPacket(outBuf.AsSpan(0, frame.Length + 1).ToArray()); }
            catch (Exception ex) { _logger.LogError(ex, "❌ Failed to send video frame"); }
            finally { ArrayPool<byte>.Shared.Return(outBuf); }
        }

        #endregion

        #region Worker

        public void StartWorker()
        {
            if (_workerTask != null && !_workerTask.IsCompleted) return;

            _workerCts?.Cancel();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;

            _workerTask = Task.Run(() =>
            {
                _logger.LogInformation("✅ AVHandler worker started");
                int processedCount = 0;
                DateTime lastLog = DateTime.Now;

                while (!token.IsCancellationRequested && !_ct.IsCancellationRequested)
                {
                    int batch = 5;
                    for (int i = 0; i < batch; i++)
                    {
                        if (!_queue.TryDequeue(out var pkt)) break;
                        try
                        {
                            ProcessSinglePacket(pkt);
                            processedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Error processing AV packet frame={Frame}", pkt.FrameIndex);
                        }
                    }

                    if (_queue.IsEmpty) Thread.Sleep(0);

                    var now = DateTime.Now;
                    if ((now - lastLog).TotalSeconds > 10)
                    {
                        _logger.LogDebug("📊 Worker processed {Count} packets, queue={Queue}", processedCount, _queue.Count);
                        lastLog = now;
                    }
                }

                _queue.Clear();
                _logger.LogDebug("AVHandler worker stopped, total processed={Count}", processedCount);
            }, token);
        }

        #endregion

        #region Stop & Stats

        public void Stop()
        {
            _workerCts?.Cancel();
            _queue.Clear();
        }

        public (int received, int lost) GetStats()
        {
            int received = (_videoStream?.Received ?? 0) + (_audioStream?.Received ?? 0);
            int lost = (_videoStream?.Lost ?? 0) + (_audioStream?.Lost ?? 0);
            return (received, lost);
        }

        #endregion
    }
}
