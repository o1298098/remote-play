using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Services.Streaming.Pipeline
{
    /// <summary>
    /// Audio Pipeline - 负责音频包的处理
    /// 设计目标：
    /// 1. 独立线程处理（不阻塞 Ingest 和 Video）
    /// 2. 低延迟（音频不经过 ReorderQueue）
    /// 3. 快速通道（优先级高于视频）
    /// </summary>
    public sealed class AudioPipeline : IDisposable
    {
        private readonly ILogger<AudioPipeline> _logger;
        private readonly ChannelReader<AVPacket> _inputReader;
        private readonly Channel<ProcessedFrame> _outputChannel;
        private readonly AudioReceiver? _audioReceiver;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _workerTask;

        // 配置
        private string? _detectedCodec;
        private Action<int>? _frameLossCallback;
        private StreamCipher? _cipher;  // ⚠️ 解密密钥（与旧的 AVHandler 一致）

        // 统计
        private long _totalReceived;
        private long _totalProcessed;
        private long _totalDropped;
        private long _framesComplete;

        public AudioPipeline(
            ILogger<AudioPipeline> logger,
            ChannelReader<AVPacket> inputReader,
            ILoggerFactory loggerFactory,
            int outputCapacity = 512)
        {
            _logger = logger;
            _inputReader = inputReader;

            _outputChannel = Channel.CreateBounded<ProcessedFrame>(new BoundedChannelOptions(outputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

            // 初始化 AudioReceiver
            _audioReceiver = new AudioReceiver(loggerFactory.CreateLogger<AudioReceiver>());

            _workerTask = Task.Run(WorkerLoop, _cts.Token);
        }

        #region Public API

        /// <summary>
        /// 获取输出 Channel
        /// </summary>
        public ChannelReader<ProcessedFrame> OutputReader => _outputChannel.Reader;

        /// <summary>
        /// 设置音频 Header
        /// </summary>
        public void SetHeader(byte[]? audioHeader)
        {
            _audioReceiver?.SetHeader(audioHeader);
        }

        /// <summary>
        /// 设置音频编解码器
        /// </summary>
        public void SetAudioCodec(string codec)
        {
            _detectedCodec = codec;
        }

        /// <summary>
        /// 设置帧丢失回调
        /// </summary>
        public void SetFrameLossCallback(Action<int>? callback)
        {
            _frameLossCallback = callback;
            _audioReceiver?.SetFrameLossCallback(callback);
        }

        /// <summary>
        /// 设置解密密钥（与旧的 AVHandler 一致）
        /// </summary>
        public void SetCipher(StreamCipher? cipher)
        {
            _cipher = cipher;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public AudioStats GetStats()
        {
            return new AudioStats
            {
                TotalReceived = Interlocked.Read(ref _totalReceived),
                TotalProcessed = Interlocked.Read(ref _totalProcessed),
                TotalDropped = Interlocked.Read(ref _totalDropped),
                FramesComplete = Interlocked.Read(ref _framesComplete),
                OutputQueueSize = _outputChannel.Reader.Count
            };
        }

        #endregion

        #region Worker Loop

        private async Task WorkerLoop()
        {
            _logger.LogInformation("✅ AudioPipeline worker started");

            try
            {
                await foreach (var packet in _inputReader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        Interlocked.Increment(ref _totalReceived);

                        // 自动检测编解码器
                        if (_detectedCodec == null)
                        {
                            DetectAudioCodec(packet);
                        }

                        // 直接处理（音频不需要重排序）
                        HandlePacket(packet);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ AudioPipeline processing error, frame={Frame}", packet.FrameIndex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AudioPipeline worker exception");
            }
            finally
            {
                _logger.LogInformation("✅ AudioPipeline worker exited");
            }
        }

        #endregion

        #region Packet Processing

        private void HandlePacket(AVPacket packet)
        {
            try
            {
                if (_audioReceiver == null)
                {
                    _logger.LogWarning("⚠️ AudioReceiver is null");
                    return;
                }

                // ⚠️ 关键修复：解密已在 IngestPipeline 中完成（串行处理，保证 keyPos 顺序）
                // packet.Data 已经是解密后的数据
                _audioReceiver.ProcessPacket(packet, packet.Data, (frame) =>
                {
                    Interlocked.Increment(ref _totalProcessed);
                    Interlocked.Increment(ref _framesComplete);

                    // 创建处理后的帧
                    var processedFrame = new ProcessedFrame
                    {
                        Type = FrameType.Audio,
                        FrameIndex = packet.FrameIndex,
                        Data = frame,
                        Recovered = false,
                        Timestamp = DateTime.UtcNow,
                        IsKeyFrame = false
                    };

                    // 推送到输出队列（非阻塞）
                    if (!_outputChannel.Writer.TryWrite(processedFrame))
                    {
                        Interlocked.Increment(ref _totalDropped);
                        _logger.LogWarning("⚠️ AudioPipeline output queue full, dropping frame={Frame}",
                            packet.FrameIndex);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ HandlePacket error, frame={Frame}", packet.FrameIndex);
            }
        }

        #endregion

        #region Decryption

        /// <summary>
        /// 解密包数据（与旧的 AVHandler 完全一致）
        /// </summary>
        private byte[] DecryptPacket(AVPacket packet)
        {
            var data = packet.Data;
            if (_cipher != null && data.Length > 0 && packet.KeyPos > 0)
            {
                try 
                { 
                    data = _cipher.Decrypt(data, (int)packet.KeyPos); 
                }
                catch (Exception ex) 
                { 
                    _logger.LogError(ex, "❌ Decrypt failed frame={Frame}", packet.FrameIndex); 
                }
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
            {
                _logger.LogWarning("⚠️ Unknown audio codec 0x{Codec:X2}, defaulting to opus", packet.Codec);
            }

            _detectedCodec = codec;
            _logger.LogInformation("🔊 Detected audio codec: {Codec}", codec);
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            _cts.Cancel();

            try
            {
                _workerTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ AudioPipeline dispose error");
            }

            _outputChannel.Writer.Complete();
            _cts.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Audio Pipeline 统计信息
    /// </summary>
    public struct AudioStats
    {
        public long TotalReceived { get; set; }
        public long TotalProcessed { get; set; }
        public long TotalDropped { get; set; }
        public long FramesComplete { get; set; }
        public int OutputQueueSize { get; set; }
    }
}

