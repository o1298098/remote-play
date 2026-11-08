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
        private int _loggedKeyPosWraps;

        // key_pos 回绕后的位置模型判断：回绕前学习音频源单元的首字节（Opus TOC，整场基本不变），
        // 回绕后分别用两种模型解密音频包，哪种能解出该字节就采用哪种
        private const int AudioFirstByteStreakRequired = 20;
        private const int KeyPosModelVotesRequired = 3;
        private int _audioFirstByteCandidate = -1;
        private int _audioFirstByteStreak;
        private int _expectedAudioFirstByte = -1;
        private bool _keyPosModelDecided;
        private int _currentModelVotes;
        private int _alternateModelVotes;
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

        /// <summary>
        /// 回绕前学习音频首字节；回绕后用它判断应采用哪种 key_pos 模型。返回本包应使用的解密结果。
        /// </summary>
        private byte[] ObserveAudioForKeyPosModel(AVPacket packet, byte[] decrypted, int wraps)
        {
            if (wraps == 0)
            {
                if (_expectedAudioFirstByte < 0)
                {
                    int first = decrypted[0];
                    if (first == _audioFirstByteCandidate)
                    {
                        if (++_audioFirstByteStreak >= AudioFirstByteStreakRequired)
                        {
                            _expectedAudioFirstByte = first;
                        }
                    }
                    else
                    {
                        _audioFirstByteCandidate = first;
                        _audioFirstByteStreak = 1;
                    }
                }
                return decrypted;
            }

            if (_keyPosModelDecided || _expectedAudioFirstByte < 0 || _cipher == null)
            {
                return decrypted;
            }

            bool currentExtended = _cipher.RemoteExtendedKeyPos;
            var alternate = _cipher.DecryptWithModel(packet.Data, packet.KeyPos, !currentExtended);
            bool currentOk = decrypted[0] == _expectedAudioFirstByte;
            bool alternateOk = alternate.Length > 0 && alternate[0] == _expectedAudioFirstByte;

            if (currentOk && !alternateOk) _currentModelVotes++;
            else if (alternateOk && !currentOk) _alternateModelVotes++;

            if (_alternateModelVotes >= KeyPosModelVotesRequired)
            {
                _cipher.RemoteExtendedKeyPos = !currentExtended;
                _keyPosModelDecided = true;
                _logger.LogWarning("🔐 key_pos wrapped, switching to {Model} position model to continue decrypting", !currentExtended ? "64-bit continuous" : "32-bit wrapping");
                return alternate;
            }

            if (_currentModelVotes >= KeyPosModelVotesRequired)
            {
                _keyPosModelDecided = true;
                _logger.LogWarning("🔐 key_pos wrapped, confirmed {Model} position model", currentExtended ? "64-bit continuous" : "32-bit wrapping");
            }

            return alternateOk && !currentOk ? alternate : decrypted;
        }

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
                                int wraps = _cipher.RemoteKeyPosWrapCount;
                                if (wraps != _loggedKeyPosWraps)
                                {
                                    _loggedKeyPosWraps = wraps;
                                    _logger.LogWarning("🔁 Host key_pos wrapped (wrap #{Count}, roughly once per 4 GB of data) keyPos={KeyPos}, expected audio first byte={Toc}",
                                        wraps, packet.KeyPos, _expectedAudioFirstByte >= 0 ? $"0x{_expectedAudioFirstByte:X2}" : "unknown");
                                }
                                if (packet.Type == HeaderType.AUDIO && packet.UnitsSrc > 0 && decryptedData.Length > 0)
                                {
                                    decryptedData = ObserveAudioForKeyPosModel(packet, decryptedData, wraps);
                                }
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

