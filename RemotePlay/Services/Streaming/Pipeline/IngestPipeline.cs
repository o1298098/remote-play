using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RemotePlay.Models.Streaming;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Protocol;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Services.Streaming.Pipeline
{
    /// <summary>
    /// Ingest Pipeline - 负责接收、解析和解密 AV 包
    /// 设计目标：
    /// 1. 高吞吐量（使用 Channel 而非 ConcurrentQueue）
    /// 2. 非阻塞（解密在独立线程）
    /// 3. 最小延迟
    /// </summary>
    public sealed class IngestPipeline : IDisposable
    {
        private readonly ILogger<IngestPipeline> _logger;
        private readonly string _hostType;
        private readonly Channel<byte[]> _inputChannel;
        private readonly Channel<AVPacket> _outputChannel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _workerTask;
        private StreamCipher? _cipher;

        // 统计
        private long _totalReceived;
        private long _totalParsed;
        private long _totalDecrypted;
        private long _parseErrors;
        private long _decryptErrors;

        public IngestPipeline(
            ILogger<IngestPipeline> logger,
            string hostType,
            int inputCapacity = 2048,
            int outputCapacity = 2048)
        {
            _logger = logger;
            _hostType = hostType;

            // 使用 Channel 实现高性能无锁队列
            // DropWrite 策略：当满时丢弃新数据（保护内存）
            _inputChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(inputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });

            _outputChannel = Channel.CreateBounded<AVPacket>(new BoundedChannelOptions(outputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = true
            });

            _workerTask = Task.Run(WorkerLoop, _cts.Token);
        }

        #region Public API

        /// <summary>
        /// 推送原始网络数据（非阻塞）
        /// </summary>
        public bool TryPushRawData(byte[] data)
        {
            Interlocked.Increment(ref _totalReceived);
            return _inputChannel.Writer.TryWrite(data);
        }

        /// <summary>
        /// 获取输出 Channel（供下游 Pipeline 读取）
        /// </summary>
        public ChannelReader<AVPacket> OutputReader => _outputChannel.Reader;

        public void SetCipher(StreamCipher? cipher)
        {
            _cipher = cipher;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public IngestStats GetStats()
        {
            return new IngestStats
            {
                TotalReceived = Interlocked.Read(ref _totalReceived),
                TotalParsed = Interlocked.Read(ref _totalParsed),
                TotalDecrypted = Interlocked.Read(ref _totalDecrypted),
                ParseErrors = Interlocked.Read(ref _parseErrors),
                DecryptErrors = Interlocked.Read(ref _decryptErrors),
                InputQueueSize = _inputChannel.Reader.Count,
                OutputQueueSize = _outputChannel.Reader.Count
            };
        }

        #endregion

        #region Worker Loop

        private async Task WorkerLoop()
        {
            try
            {
                await foreach (var rawData in _inputChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        if (!AVPacket.TryParse(rawData, _hostType, out var packet))
                        {
                            Interlocked.Increment(ref _parseErrors);
                            continue;
                        }

                        Interlocked.Increment(ref _totalParsed);

                        if (_cipher != null && packet.Data.Length > 0 && packet.KeyPos > 0)
                        {
                            try
                            {
                                var decryptedData = _cipher.Decrypt(packet.Data, packet.KeyPos);
                                    if (decryptedData != null && decryptedData.Length == packet.Data.Length)
                                    {
                                        int dataStart = rawData.Length - packet.Data.Length;
                                        var newBuffer = new byte[dataStart + decryptedData.Length];
                                        Array.Copy(rawData, 0, newBuffer, 0, dataStart);
                                        Array.Copy(decryptedData, 0, newBuffer, dataStart, decryptedData.Length);
                                        
                                        if (AVPacket.TryParse(newBuffer, _hostType, out var decryptedPacket))
                                        {
                                            packet = decryptedPacket;
                                            Interlocked.Increment(ref _totalDecrypted);
                                        }
                                        else
                                        {
                                            Interlocked.Increment(ref _decryptErrors);
                                        }
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref _decryptErrors);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref _decryptErrors);
                                    _logger.LogError(ex, "Decrypt failed frame={Frame}, keyPos={KeyPos}", packet.FrameIndex, packet.KeyPos);
                                }
                        }

                        if (!_outputChannel.Writer.TryWrite(packet))
                        {
                            _logger.LogWarning("IngestPipeline output queue full, dropping packet frame={Frame}", packet.FrameIndex);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "IngestPipeline processing error");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IngestPipeline worker exception");
            }
            finally
            {
                _logger.LogInformation("IngestPipeline worker exited");
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            _inputChannel.Writer.Complete();
            _cts.Cancel();

            try
            {
                _workerTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IngestPipeline dispose error");
            }

            _outputChannel.Writer.Complete();
            _cts.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Ingest Pipeline 统计信息
    /// </summary>
    public struct IngestStats
    {
        public long TotalReceived { get; set; }
        public long TotalParsed { get; set; }
        public long TotalDecrypted { get; set; }
        public long ParseErrors { get; set; }
        public long DecryptErrors { get; set; }
        public int InputQueueSize { get; set; }
        public int OutputQueueSize { get; set; }
    }
}

