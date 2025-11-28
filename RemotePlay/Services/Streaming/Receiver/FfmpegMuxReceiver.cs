using RemotePlay.Models.PlayStation;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Concentus;
using Concentus.Structs;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed class FfmpegMuxReceiver : IAVReceiver, IDisposable
    {
        private readonly ILogger<FfmpegMuxReceiver>? _logger;
        private readonly string _ffmpegPath;
        private readonly string _output;
        private readonly string _extraArgs;
        private readonly bool _enableAudio;
        private bool _audioActuallyEnabled;
        private readonly bool _useTcp;
        private readonly string _videoCodec;
        private readonly string _audioCodec;
        private readonly bool _genPts;
        private readonly bool _enableVideo;

        private readonly string _workDir;
        private readonly string _fifoVideo;
        private readonly string _fifoAudio;
        private int _videoPort;
        private int _audioPort;
        private TcpListener? _videoListener;
        private TcpListener? _audioListener;

        private Stream? _videoWriter;
        private Stream? _audioWriter;
        private TcpClient? _videoClient;
        private TcpClient? _audioClient;
        private Process? _proc;
        private bool _disposed;
        private int _videoPacketCount;
        private int _audioPacketCount;
        private string _detectedVideoFormat = "h264";
        private string _detectedAudioFormat = "";
        private int _audioSampleRate = 48000;
        private int _audioChannels = 2;
        private int _audioFrameSize = 480; // 默认帧大小（从 audio header 获取）
        private bool _ffmpegRestartNeeded = false;
        private readonly object _procLock = new object();
        
        private bool _audioConnectionWarningLogged = false;
        private int _videoWriteErrorCount = 0;
        
        // Opus 解码器（用于将 Opus 帧解码为 PCM）
        private IOpusDecoder? _opusDecoder;
        private readonly object _opusDecoderLock = new object();

        public FfmpegMuxReceiver(string? output = null, bool enableAudio = true, string? ffmpegPath = null, string? extraArgs = null, bool useTcp = true, string? videoCodec = null, string? audioCodec = null, bool genPts = true, bool enableVideo = true, ILogger<FfmpegMuxReceiver>? logger = null)
        {
            _logger = logger;
            _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath!;
            _enableVideo = enableVideo;
            _enableAudio = !enableVideo ? true : enableAudio; // 纯音频模式强制启用音频
            _audioActuallyEnabled = _enableAudio;
            _extraArgs = extraArgs?.Trim() ?? string.Empty;
            _useTcp = useTcp;
            _videoCodec = string.IsNullOrWhiteSpace(videoCodec) ? "copy" : videoCodec!;
            _audioCodec = string.IsNullOrWhiteSpace(audioCodec) ? "aac" : audioCodec!;
            _genPts = genPts;
            
            var baseDir = Directory.GetCurrentDirectory();
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            _output = string.IsNullOrWhiteSpace(output)
                ? Path.Combine(baseDir, $"remoteplay_{ts}.mkv")
                : Path.GetFullPath(output);
            
            var outDir = Path.GetDirectoryName(_output);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            _workDir = Path.Combine(Path.GetTempPath(), $"remoteplay_ffmpeg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_workDir);
            _fifoVideo = Path.Combine(_workDir, "video.h264");
            _fifoAudio = Path.Combine(_workDir, "audio.aac");

            if (!_useTcp)
            {
                CreateFifo(_fifoVideo);
                if (_enableAudio) CreateFifo(_fifoAudio);
            }

            try
            {
            if (_useTcp)
            {
                // 关键优化：先启动监听器，然后异步准备接受连接
                // 这样 FFmpeg 启动时连接已经准备好，可以立即开始处理
                StartListeners();
                
                // 异步接受连接（在后台等待 FFmpeg 连接）
                // 使用 Task 而不是阻塞，这样 FFmpeg 可以立即启动
                var acceptTask = Task.Run(() =>
                {
                    try
                    {
                        AcceptClients(timeoutMs: 30000);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error accepting clients");
                    }
                });
                
                // ✅ 优化：使用 Task.Delay 替代 Thread.Sleep，避免阻塞线程
                Task.Delay(200).GetAwaiter().GetResult();
                
                // 启动 FFmpeg（此时 listener 已经准备好，FFmpeg 可以立即连接）
                StartFfmpeg();
                
                // 不等待 acceptTask，让它在后台运行
                // FFmpeg 会等待连接建立，但不会阻塞整个流程
            }
            else
            {
                StartFfmpeg();
                OpenWriters();
                }
                _logger?.LogInformation("FFmpeg started successfully: {Output}", _output);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start FFmpeg");
                throw;
            }
        }

        private static void CreateFifo(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }

            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/mkfifo",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(path);

            using var p = Process.Start(psi);
            p!.WaitForExit();
            if (p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd();
                if (string.IsNullOrWhiteSpace(err)) err = p.StandardOutput.ReadToEnd();
                throw new InvalidOperationException($"Failed to create FIFO: {err}");
            }
        }

        private void StartFfmpeg()
        {
            // 在 StartFfmpeg 时，应该根据 _enableAudio 来决定是否包含音频
            // _audioActuallyEnabled 会在 AcceptClients 中根据实际连接结果更新
            bool includeAudio = _enableAudio;
            string fflags = _genPts ? "-fflags +genpts+nobuffer+discardcorrupt" : "-fflags nobuffer+discardcorrupt";

            string inputs;
                if (!_enableVideo)
                {
                    // 纯音频模式：只输入音频流
                    // 使用 Opus 解码器将 Opus 帧解码为 PCM，然后传给 FFmpeg
                    // 因为传入的是 PCM 数据，使用 -f s16le 格式（16-bit PCM 小端序）
                    if (_useTcp)
                    {
                        inputs = $"{fflags} -thread_queue_size 2048 -err_detect ignore_err -f s16le -ar {_audioSampleRate} -ac {_audioChannels} -i tcp://127.0.0.1:{_audioPort}";
                    }
                    else
                    {
                        inputs = $"{fflags} -thread_queue_size 2048 -err_detect ignore_err -f s16le -ar {_audioSampleRate} -ac {_audioChannels} -i \"{_fifoAudio}\"";
                    }
                    _logger?.LogInformation("Pure audio mode: Using PCM input format (s16le, {Rate}Hz, {Channels} channels) - Opus decoded by Concentus", 
                        _audioSampleRate, _audioChannels);
                }
                else
                {
                        // 正常模式：视频 + 可选音频
                        // 注意：音频数据已经通过 Opus 解码器解码为 PCM，所以使用 -f s16le 格式
                        // 使用 -err_detect ignore_err 让 FFmpeg 自动处理非关键帧起播的情况
                        // 关键：对于音视频合并，需要确保两个流都有时间戳生成（+genpts）
                        // 这样 FFmpeg 才能正确对齐和同步两个流
                        if (_useTcp)
                        {
                            // 使用标准 TCP URL，FFmpeg 会作为客户端连接
                            // 注意：listener 已经在 StartListeners() 中启动，FFmpeg 可以立即连接
                            // 视频流：使用 +genpts 生成时间戳，-r 60 指定帧率
                            inputs = $"{fflags} -thread_queue_size 2048 -err_detect ignore_err -analyzeduration 10000000 -probesize 10000000 -f {_detectedVideoFormat} -r 60 -i tcp://127.0.0.1:{_videoPort}";
                            if (includeAudio)
                            {
                                // 音频流：使用 +genpts 生成时间戳，-ar 指定采样率
                                // 注意：两个流都需要 +genpts，这样 FFmpeg 才能基于时间戳对齐
                                // 音频时间戳基于采样率和帧大小自动生成
                                inputs += $" {fflags} -thread_queue_size 2048 -err_detect ignore_err -f s16le -ar {_audioSampleRate} -ac {_audioChannels} -i tcp://127.0.0.1:{_audioPort}";
                            }
                        }
                    else
                    {
                        inputs = $"{fflags} -thread_queue_size 2048 -err_detect ignore_err -analyzeduration 10000000 -probesize 10000000 -f {_detectedVideoFormat} -r 60 -i \"{_fifoVideo}\"";
                        if (includeAudio)
                        {
                            // 音频已经解码为 PCM，使用 s16le 格式
                            inputs += $" {fflags} -thread_queue_size 2048 -err_detect ignore_err -f s16le -ar {_audioSampleRate} -ac {_audioChannels} -i \"{_fifoAudio}\"";
                        }
                    }
                }

            string map;
            if (!_enableVideo)
            {
                // 纯音频模式：只映射音频流
                map = "-map 0:a:0";
            }
            else
            {
                // 正常模式：视频 + 可选音频
                map = "-map 0:v:0" + (includeAudio ? " -map 1:a:0?" : string.Empty);
            }
            
            bool isHls = _output.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            
            string codecs;
            if (!_enableVideo)
            {
                // 纯音频模式：只配置音频编解码器，禁用视频
                // 转为 AAC 以获得更好的兼容性（HLS + 纯音频 + Opus 的浏览器支持有限）
                codecs = $"-vn -c:a aac -b:a 128k";
            }
            else
            {
                // 正常模式：视频 + 可选音频
                string videoCodecResolved = isHls ? "copy" : (string.IsNullOrWhiteSpace(_videoCodec) ? "copy" : _videoCodec);
                if (!isHls && string.Equals(videoCodecResolved, "h264", StringComparison.OrdinalIgnoreCase))
                videoCodecResolved = "copy";
            
            var x264LowLatency = videoCodecResolved.StartsWith("libx264", StringComparison.OrdinalIgnoreCase)
                ? " -preset ultrafast -tune zerolatency -g 60 -keyint_min 60 -sc_threshold 0 -bf 0 -pix_fmt yuv420p"
                : string.Empty;
            
                string audioCodecResolved = isHls ? "copy" : _audioCodec;
                
                // 音视频同步参数
                // 对于从 TCP 流输入的实时音视频，两个流都没有原始时间戳
                // +genpts 会为每个流生成时间戳：
                //   - 视频：基于 -r 60 帧率生成（每帧 1/60 秒）
                //   - 音频：基于采样率（48000Hz）和帧大小自动生成
                // 
                // -vsync 参数控制输出时的同步方式：
                //   - passthrough (0): 直接传递时间戳，不调整（适用于已有时间戳）
                //   - cfr (1): 固定帧率，调整帧率到目标值（适用于实时流）
                //   - vfr (2): 可变帧率，保持原始帧率
                // 对于实时流，使用 -vsync 1 (cfr) 让 FFmpeg 基于帧率对齐时间戳
                // 或者使用 -fps_mode passthrough（已在 formatArgs 中设置）
                
                // 注意：对于 HLS 输出，FFmpeg 会自动处理音视频同步
                // 如果音频和视频启动时间不同，FFmpeg 会基于 genpts 生成的时间戳自动对齐
                // 如果还有问题，可以添加 -itsoffset 来手动调整音频偏移
                
                codecs = $"-c:v {videoCodecResolved}{x264LowLatency}" + (includeAudio ? $" -c:a {audioCodecResolved}" : " -an");
            }
            
            string formatArgs = "";
            bool hasHlsFormat = _extraArgs.Contains("-f hls", StringComparison.OrdinalIgnoreCase);
            
            // 从 _extraArgs 中提取 hls_time 参数值（用于日志）
            string? hlsTimeValue = null;
            if (isHls && hasHlsFormat)
            {
                var hlsTimeMatch = System.Text.RegularExpressions.Regex.Match(_extraArgs, @"-hls_time\s+(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (hlsTimeMatch.Success)
                {
                    hlsTimeValue = hlsTimeMatch.Groups[1].Value;
                }
            }
            
            if (isHls && !hasHlsFormat)
            {
                var segDir = Path.GetDirectoryName(_output);
                if (!_enableVideo)
                {
                    // 纯音频模式：使用传统 mpegts 格式，fMP4 对纯音频支持不好
                    var segPattern = string.IsNullOrEmpty(segDir) 
                        ? "segment_%05d.ts" 
                        : Path.Combine(segDir, "segment_%05d.ts");
                    formatArgs = $"-f hls -hls_time 1 -hls_list_size 6 -hls_flags delete_segments+append_list -start_number 0 -hls_segment_filename \"{segPattern}\"";
                    hlsTimeValue = "1"; // 硬编码的默认值
                }
                else
                {
                    // 正常模式：使用 fMP4
                var segPattern = string.IsNullOrEmpty(segDir) 
                    ? "segment_%05d.m4s" 
                    : Path.Combine(segDir, "segment_%05d.m4s");
                formatArgs = $"-f hls -hls_time 1 -hls_list_size 6 -hls_flags independent_segments+delete_segments+append_list -hls_segment_type fmp4 -start_number 0 -hls_segment_filename \"{segPattern}\"";
                    hlsTimeValue = "1"; // 硬编码的默认值
                }
            }
            else if (!isHls && !hasHlsFormat)
                {
                    formatArgs = string.Join(' ', new[]
                    {
                        "-f matroska",
                        "-flush_packets 1",
                        "-max_delay 0",
                        "-muxpreload 0",
                        "-muxdelay 0"
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            string finalExtraArgs = _extraArgs;
            if (isHls)
            {
                if (OperatingSystem.IsWindows() && finalExtraArgs.Contains("-hls_segment_filename"))
                    {
                        finalExtraArgs = finalExtraArgs.Replace('\'', '"');
                    }
                if (!finalExtraArgs.Contains("-flush_packets", StringComparison.OrdinalIgnoreCase))
                    finalExtraArgs += " -flush_packets 1";
                if (!finalExtraArgs.Contains("-max_delay", StringComparison.OrdinalIgnoreCase))
                    finalExtraArgs += " -max_delay 0";
                if (!finalExtraArgs.Contains("-muxpreload", StringComparison.OrdinalIgnoreCase))
                    finalExtraArgs += " -muxpreload 0";
                if (!finalExtraArgs.Contains("-muxdelay", StringComparison.OrdinalIgnoreCase))
                    finalExtraArgs += " -muxdelay 0";
            }

            var args = $"-hide_banner -loglevel error {inputs} {map} {codecs} {formatArgs} {finalExtraArgs} -y \"{_output}\"".Trim();

            // 记录 HLS 切片时长参数（如果可用）
            if (isHls && !string.IsNullOrEmpty(hlsTimeValue))
            {
                _logger?.LogInformation("HLS segment time (切片时长): {SegmentTime} seconds", hlsTimeValue);
            }

            _logger?.LogInformation("Starting FFmpeg with command: {Command} {Args}", _ffmpegPath, args);

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                if (!_proc.Start()) throw new InvalidOperationException("Failed to start FFmpeg process");
                    _proc.ErrorDataReceived += (s, e) => 
                    { 
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        _logger?.LogWarning("[FFmpeg] {Message}", e.Data);
                    };
                    _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start FFmpeg");
                throw new InvalidOperationException("Failed to start FFmpeg. Please ensure FFmpeg is installed and in PATH", ex);
            }
        }

        private void OpenWriters()
        {
            if (!_useTcp)
            {
                _videoWriter = new FileStream(_fifoVideo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                if (_enableAudio)
                {
                    _audioWriter = new FileStream(_fifoAudio, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
            }
        }

        private void StartListeners()
        {
            if (_enableVideo)
            {
                _videoPort = GetFreeTcpPort();
                _videoListener = new TcpListener(IPAddress.Loopback, _videoPort);
                _videoListener.Start();
            }
            
            // 如果启用了音频，应该启动音频 listener（不管是纯音频模式还是视频+音频模式）
            // 注意：应该根据 _enableAudio 来判断，而不是 _audioActuallyEnabled
            // _audioActuallyEnabled 会在连接失败后被设置为 false，但 listener 应该在启动时就准备好
            if (_enableAudio || !_enableVideo)
            {
                _audioPort = GetFreeTcpPort();
                _audioListener = new TcpListener(IPAddress.Loopback, _audioPort);
                _audioListener.Start();
            }
        }

        private void AcceptClients(int timeoutMs = 30000) // 增加超时到30秒，给FFmpeg更多时间连接
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            try
            {
                // 纯音频模式：只等待音频连接
                if (!_enableVideo)
                {
                    if (_audioListener == null) throw new InvalidOperationException("Audio listener not started");
                    
                    _logger?.LogInformation("Waiting for FFmpeg to connect to audio port: {Port}", _audioPort);
                    
                    while (DateTime.UtcNow < deadline)
                    {
                        if (_audioListener != null && _audioListener.Pending())
                        {
                            _audioClient = _audioListener.AcceptTcpClient();
                            if (_audioClient != null)
                            {
                                _audioWriter = _audioClient.GetStream();
                                if (_audioWriter != null)
                                {
                                    _logger?.LogInformation("Audio connection established successfully on port {Port}", _audioPort);
                                    break;
                                }
                            }
                        }
                        // ✅ 优化：使用 Task.Delay 替代 Thread.Sleep，避免阻塞线程
                        Task.Delay(20).GetAwaiter().GetResult();
                    }
                    if (_audioWriter == null)
                    {
                        _logger?.LogError("Audio connection timeout after {Timeout}ms on port {Port}", timeoutMs, _audioPort);
                        throw new InvalidOperationException($"Audio connection timeout: {_audioPort}");
                    }
                    return;
                }
                
                // 正常模式：并行等待视频和音频连接（FFmpeg 可能同时连接两个端口）
                if (_videoListener == null) throw new InvalidOperationException("Video listener not started");
                
                _logger?.LogInformation("Waiting for FFmpeg to connect - Video port: {VideoPort}, Audio port: {AudioPort} (timeout: {Timeout}ms)", 
                    _videoPort, _enableAudio && _audioListener != null ? _audioPort : -1, timeoutMs);
                
                bool videoConnected = false;
                bool audioConnected = !(_enableAudio && _audioListener != null); // 如果不需要音频，认为已连接
                DateTime? videoConnectedTime = null;
                DateTime? audioConnectedTime = null;
                
                // 检查是否已经连接（避免重复连接）
                if (_videoWriter != null && _videoClient != null && _videoClient.Connected)
                {
                    videoConnected = true;
                    _logger?.LogInformation("Video connection already established");
                }
                if (_enableAudio && _audioWriter != null && _audioClient != null && _audioClient.Connected)
                {
                    audioConnected = true;
                    _audioActuallyEnabled = true;
                    _logger?.LogInformation("Audio connection already established");
                }
                
                // 视频连接必须成功，音频连接可以稍后建立（FFmpeg 可能先连接视频，再连接音频）
                while (DateTime.UtcNow < deadline && !_disposed)
                {
                    try
                    {
                        // 检查视频连接（必须成功）
                        // 注意：在异步任务中，_videoListener 可能被其他线程设置为 null
                        if (!videoConnected && _videoListener != null && _videoListener.Server != null && _videoListener.Server.IsBound && _videoListener.Pending())
                        {
                            _videoClient = _videoListener.AcceptTcpClient();
                            _videoWriter = _videoClient.GetStream();
                            videoConnectedTime = DateTime.UtcNow;
                            _logger?.LogInformation("Video connection established successfully on port {Port}", _videoPort);
                            videoConnected = true;
                        }
                        
                        // 检查音频连接（如果需要）
                        // 注意：在异步任务中，_audioListener 可能被其他线程设置为 null
                        if (!audioConnected && _enableAudio && _audioListener != null && _audioListener.Server != null)
                        {
                            if (!_audioListener.Server.IsBound)
                            {
                                audioConnected = true; // Listener 已关闭，跳过
                            }
                            else if (_audioListener.Pending())
                            {
                                _audioClient = _audioListener.AcceptTcpClient();
                                _audioWriter = _audioClient.GetStream();
                                audioConnectedTime = DateTime.UtcNow;
                                _logger?.LogInformation("Audio connection established successfully on port {Port}", _audioPort);
                                _audioActuallyEnabled = true; // 连接成功，确认音频可用
                                audioConnected = true;
                            }
                        }
                        
                        // 如果视频已连接，音频也连接（或不需要），可以退出
                        if (videoConnected && audioConnected)
                        {
                            if (videoConnectedTime.HasValue && audioConnectedTime.HasValue)
                            {
                                var delay = (audioConnectedTime.Value - videoConnectedTime.Value).TotalMilliseconds;
                                if (delay > 0)
                                {
                                    _logger?.LogInformation("Both connections established - Audio delay: {Delay}ms after video", delay);
                                }
                            }
                            break; // 立即退出循环，不再尝试接受连接
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        // Socket 操作被中断（通常是 listener 被关闭），这是正常的，忽略
                        // 但在退出前，检查连接是否已经建立（可能在异常之前建立）
                        if (!videoConnected && _videoWriter != null && _videoClient != null && _videoClient.Connected)
                        {
                            videoConnected = true;
                        }
                        if (!audioConnected && _enableAudio && _audioWriter != null && _audioClient != null && _audioClient.Connected)
                        {
                            audioConnected = true;
                            _audioActuallyEnabled = true;
                        }
                        
                        if (!videoConnected || (!audioConnected && _enableAudio))
                        {
                            _logger?.LogDebug("Socket accept interrupted, connections: video={Video}, audio={Audio}", 
                                videoConnected, audioConnected);
                        }
                        break; // 退出循环
                    }
                    catch (ObjectDisposedException)
                    {
                        // Listener 已被释放，退出循环
                        // 但在退出前，检查连接是否已经建立
                        if (!videoConnected && _videoWriter != null && _videoClient != null && _videoClient.Connected)
                        {
                            videoConnected = true;
                        }
                        if (!audioConnected && _enableAudio && _audioWriter != null && _audioClient != null && _audioClient.Connected)
                        {
                            audioConnected = true;
                            _audioActuallyEnabled = true;
                        }
                        _logger?.LogDebug("Listener disposed during accept, exiting");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 只在连接未建立时记录警告
                        if (!videoConnected || (!audioConnected && _enableAudio))
                        {
                            _logger?.LogWarning(ex, "Connection error during accept");
                        }
                        // 继续等待，不立即退出
                    }
                    
                    // 如果连接已建立，不需要再 sleep，直接退出
                    if (videoConnected && audioConnected)
                        break;
                    
                    // ✅ 优化：使用 Task.Delay 替代 Thread.Sleep，避免阻塞线程
                    Task.Delay(20).GetAwaiter().GetResult();
                }
                
                // 在检查超时之前，再次验证连接是否真的建立（可能在其他线程中建立）
                if (!videoConnected && _videoWriter != null && _videoClient != null && _videoClient.Connected)
                {
                    videoConnected = true;
                    _logger?.LogInformation("Video connection verified after loop exit");
                }
                
                if (!videoConnected)
                    throw new InvalidOperationException($"Video connection timeout: {_videoPort}");
                
                // 如果启用了音频但连接失败，记录警告但不抛出异常（允许视频-only 模式继续）
                // 注意：音频连接可能在 AcceptClients 之后建立，所以这里不立即标记为失败
                // 后续的等待逻辑会在 AcceptClients 之后继续尝试连接
                if (_enableAudio && _audioListener != null && !audioConnected)
                {
                    _logger?.LogWarning("Audio connection not established during AcceptClients ({Port}), will continue waiting after AcceptClients", _audioPort);
                    // 不在此时设置 _audioActuallyEnabled = false，让后续等待处理
                }
            }
            finally
            {
                // 注意：只关闭 video listener，audio listener 保持打开以便 FFmpeg 后续连接
                // FFmpeg 可能在 AcceptClients() 完成后才尝试连接音频端口
                // 只有在视频连接已建立时才关闭 listener（避免中断正在进行的连接）
                // 检查 _videoWriter 是否存在来判断连接是否已建立
                if (_videoWriter != null && _videoClient != null && _videoClient.Connected)
                {
                    try { _videoListener?.Stop(); } catch { }
                    _videoListener = null;
                }
                // audio listener 保持打开，直到 Dispose
            }
            
            // 如果启用了音频但连接还未建立，继续等待（FFmpeg 可能稍后才连接）
            // 这通常发生在 FFmpeg 启动需要时间，或者音频流还没准备好时
            if (_enableAudio && _audioListener != null && _audioWriter == null && !_disposed)
            {
                _logger?.LogInformation("Audio connection not established yet, waiting for FFmpeg to connect after AcceptClients (port {Port})", _audioPort);
                var audioDeadline = DateTime.UtcNow.AddMilliseconds(10000); // 额外等待10秒
                
                while (DateTime.UtcNow < audioDeadline && !_disposed)
                {
                    try
                    {
                        // 检查 listener 和 Server 是否有效（可能在异步任务中被设置为 null）
                        if (_audioListener == null || _audioListener.Server == null || !_audioListener.Server.IsBound)
                            break;
                        
                        if (_audioListener.Pending())
                        {
                            _audioClient = _audioListener.AcceptTcpClient();
                            _audioWriter = _audioClient.GetStream();
                            _logger?.LogInformation("Audio connection established successfully on port {Port} (after AcceptClients)", _audioPort);
                            _audioActuallyEnabled = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Audio connection error after AcceptClients");
                        break;
                    }
                    // ✅ 优化：使用 Task.Delay 替代 Thread.Sleep，避免阻塞线程
                    Task.Delay(20).GetAwaiter().GetResult();
                }
                
                if (_audioWriter == null && !_disposed)
                {
                    _logger?.LogWarning("Audio connection timeout after AcceptClients ({Port}), continuing in video-only mode", _audioPort);
                    _audioActuallyEnabled = false;
                }
            }
        }

        private static int GetFreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void SetVideoCodec(string codec)
        {
            if (codec == "hevc" && _detectedVideoFormat == "h264")
            {
                _detectedVideoFormat = "hevc";
                _ffmpegRestartNeeded = true;
                TryRestartFfmpeg();
            }
            else if (codec == "h264" && _detectedVideoFormat != "h264")
            {
                _detectedVideoFormat = "h264";
                _ffmpegRestartNeeded = true;
                TryRestartFfmpeg();
            }
        }

        public void SetAudioCodec(string codec)
        {
            // PS5 发送的是编码后的音频流（Opus/AAC），不是原始 PCM
            // 对于裸流（没有容器），FFmpeg 可能需要让自动检测，或使用特定格式
            // 注意：-f opus 和 -f aac 在某些 FFmpeg 版本中可能不支持裸流
            // 最安全的做法是让 FFmpeg 自动检测，但增加探测参数
            string ffmpegFormat = "";
            
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegFormat = "opus"; // 尝试直接使用 opus 格式
                _logger?.LogInformation("Audio codec: Opus, using -f opus format for FFmpeg (raw Opus stream)");
            }
            else if (string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase))
            {
                ffmpegFormat = "adts"; // ADTS 是 AAC 的常见容器格式
                _logger?.LogInformation("Audio codec: AAC, using ADTS format");
            }
            else
            {
                ffmpegFormat = "";
                _logger?.LogWarning("Unknown audio codec: {Codec}, will let FFmpeg auto-detect", codec);
            }
            
            if (ffmpegFormat != _detectedAudioFormat)
            {
                _detectedAudioFormat = ffmpegFormat;
                _logger?.LogInformation("Audio codec changed to: {Codec}, FFmpeg format: {Format}", codec, string.IsNullOrEmpty(ffmpegFormat) ? "auto-detect" : ffmpegFormat);
                
                lock (_procLock)
                {
                    if (_proc != null && !_proc.HasExited)
                    {
                        _ffmpegRestartNeeded = true;
                        TryRestartFfmpeg();
                    }
                }
            }
        }

        private void TryRestartFfmpeg()
        {
            Task.Run(() =>
            {
                lock (_procLock)
                {
                    if (!_ffmpegRestartNeeded)
                        return;
                    _ffmpegRestartNeeded = false;

                    try
                    {
                        try { _videoWriter?.Flush(); } catch { }
                        try { _audioWriter?.Flush(); } catch { }
                        try { _videoWriter?.Dispose(); } catch { }
                        try { _audioWriter?.Dispose(); } catch { }
                        _videoWriter = null;
                        _audioWriter = null;

                        try { _videoClient?.Dispose(); } catch { }
                        try { _audioClient?.Dispose(); } catch { }
                        _videoClient = null;
                        _audioClient = null;

                        try { _videoListener?.Stop(); } catch { }
                        try { _audioListener?.Stop(); } catch { }
                        _videoListener = null;
                        _audioListener = null;

                        try
                        {
                            if (_proc != null && !_proc.HasExited)
                                _proc.Kill(entireProcessTree: true);
                        }
                        catch { }
                        finally
                        {
                            try { _proc?.Dispose(); } catch { }
                        }

                        // 重置音频状态（因为可能在上一次连接超时后被设置为 false）
                        _audioActuallyEnabled = _enableAudio;
                        _audioConnectionWarningLogged = false;
                        
                        StartListeners();
                        StartFfmpeg();
                        AcceptClients();
                        
                        _videoPacketCount = 0;
                        _audioPacketCount = 0;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to restart FFmpeg");
                    }
                }
            });
        }

        public void OnStreamInfo(byte[] videoHeader, byte[] audioHeader)
        {
            try
            {
                if (videoHeader != null && videoHeader.Length > 0)
                {
                    string? detectedCodec = DetectCodecFromVideoHeader(videoHeader);
                    if (detectedCodec != null && detectedCodec != _detectedVideoFormat)
                    {
                        _detectedVideoFormat = detectedCodec;
                        
                        lock (_procLock)
                        {
                            if (_proc != null && !_proc.HasExited)
                            {
                                _ffmpegRestartNeeded = true;
                                try { _audioListener?.Stop(); } catch { }
                                TryRestartFfmpeg();
                            }
                        }
                    }
                }
                
                if (_enableAudio && audioHeader != null && audioHeader.Length >= 14)
                {
                    int channels = audioHeader[0];
                    int bits = audioHeader[1];
                    int rate = (audioHeader[2] << 24) | (audioHeader[3] << 16) | (audioHeader[4] << 8) | audioHeader[5];
                    int frameSize = (audioHeader[6] << 24) | (audioHeader[7] << 16) | (audioHeader[8] << 8) | audioHeader[9];
                    
                    _logger?.LogInformation("Audio config - Channels: {Channels}, Bits: {Bits}, Rate: {Rate}Hz, FrameSize: {FrameSize}", 
                        channels, bits, rate, frameSize);
                    
                    // 保存帧大小（用于 PCM 缓冲区大小计算）
                    if (frameSize > 0)
                    {
                        _audioFrameSize = frameSize;
                    }
                    
                    // 初始化 Opus 解码器（使用 libopus 直接解码 Opus 帧为 PCM）
                    if (rate > 0 && channels > 0)
                    {
                        lock (_opusDecoderLock)
                        {
                            _opusDecoder?.Dispose();
                            try
                            {
                                _opusDecoder = OpusCodecFactory.CreateDecoder(rate, channels);
                                _logger?.LogInformation("Opus decoder initialized: {Rate}Hz, {Channels} channels, FrameSize: {FrameSize} (using OpusCodecFactory)", 
                                    rate, channels, _audioFrameSize);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to initialize Opus decoder");
                                _opusDecoder = null;
                            }
                        }
                    }
                    
                    // 更新音频配置（如果与当前配置不同，则需要重启 FFmpeg）
                    bool needRestart = false;
                    if (rate > 0 && rate != _audioSampleRate)
                    {
                        _audioSampleRate = rate;
                        needRestart = true;
                    }
                    if (channels > 0 && channels != _audioChannels)
                    {
                        _audioChannels = channels;
                        needRestart = true;
                    }
                    
                    if (needRestart && !_enableVideo)
                    {
                        // 纯音频模式下，音频配置改变需要重启 FFmpeg
                        _logger?.LogInformation("Audio configuration changed, restarting FFmpeg");
                        _ffmpegRestartNeeded = true;
                        TryRestartFfmpeg();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process stream info");
            }
        }
        
        private string? DetectCodecFromVideoHeader(byte[] header)
        {
            if (header == null || header.Length < 5)
                return null;
            
            int actualHeaderLen = header.Length >= 64 ? header.Length - 64 : header.Length;
            
            for (int i = 0; i < actualHeaderLen - 4; i++)
            {
                if (i + 4 < actualHeaderLen && 
                    header[i] == 0x00 && header[i+1] == 0x00 && 
                    header[i+2] == 0x00 && header[i+3] == 0x01)
                {
                    byte nalType = header[i+4];
                    
                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                        return "hevc";
                    
                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                        return "h264";
                }
                
                if (i + 3 < actualHeaderLen && 
                    header[i] == 0x00 && header[i+1] == 0x00 && header[i+2] == 0x01)
                {
                    byte nalType = header[i+3];
                    
                    if ((nalType & 0x7E) == 0x40 || (nalType & 0x7E) == 0x42 || (nalType & 0x7E) == 0x44)
                        return "hevc";
                    
                    byte h264Type = (byte)(nalType & 0x1F);
                    if (h264Type == 7 || h264Type == 8 || h264Type == 5)
                        return "h264";
                }
            }
            
            return null;
        }

        public void OnVideoPacket(byte[] packet)
        {
            // 纯音频模式下跳过视频包
            if (!_enableVideo) return;
            
            try
            {
                if (_disposed || packet == null || packet.Length <= 1 || _videoWriter == null)
                    return;
                
                // 让 FFmpeg 自动处理关键帧检测和流探测
                // FFmpeg 会自动检测 IDR 关键帧，即使从非关键帧开始也能处理
                try
                {
                    if (_videoWriter == null)
                        return;
                    
                    _videoWriter.Write(packet, 1, packet.Length - 1);
                    
                    byte[] endPadding = new byte[64];
                    _videoWriter.Write(endPadding, 0, 64);
                    
                    if (_videoPacketCount % 10 == 0)
                    {
                        try { _videoWriter.Flush(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _videoWriteErrorCount++;
                    if (_videoWriteErrorCount <= 3 || _videoWriteErrorCount % 100 == 0)
                        _logger?.LogError(ex, "Video write failed (count: {Count})", _videoWriteErrorCount);
                }
                
                _videoPacketCount++;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to write video packet");
            }
        }
        
        public void EnterWaitForIdr()
        {
            // FFmpeg 会自动处理关键帧检测，不需要手动等待
            // 此方法保留以兼容接口，但不再执行任何操作
            _logger?.LogInformation("EnterWaitForIdr called - FFmpeg will auto-detect keyframes");
        }

        public void OnAudioPacket(byte[] packet)
        {
            if (!_enableAudio) return;
            try
            {
                if (_disposed || packet == null || packet.Length <= 1)
                    return;

                if (_audioWriter == null)
                {
                    // 如果音频 listener 还存在且还在等待连接，不要打印警告
                    // 这可能只是连接建立过程中的暂时状态
                    if (_audioListener != null && _audioListener.Server != null && _audioListener.Server.IsBound)
                    {
                        // 连接还在建立中，暂时丢弃音频包（等待连接建立）
                        return;
                    }
                    
                    // 只有在 listener 不存在或已关闭时才认为是真正的失败
                    if (!_audioConnectionWarningLogged)
                    {
                        _logger?.LogWarning("Audio connection not established, video-only mode");
                        _audioConnectionWarningLogged = true;
                    }
                    return;
                }
                
                // 使用 Opus 解码器将 Opus 帧解码为 PCM
                // packet 格式：[HeaderType.AUDIO (1 byte)] + [Opus 编码帧数据]
                byte[] opusFrame = new byte[packet.Length - 1];
                System.Buffer.BlockCopy(packet, 1, opusFrame, 0, opusFrame.Length);
                
                lock (_opusDecoderLock)
                {
                    if (_opusDecoder == null)
                    {
                        // 解码器未初始化，直接写入原始数据（让 FFmpeg 尝试自动检测）
                        if (_audioPacketCount == 0)
                        {
                            _logger?.LogWarning("Opus decoder not initialized, writing raw Opus data (FFmpeg may fail to decode)");
                        }
                        _audioWriter.Write(opusFrame, 0, opusFrame.Length);
                    }
                    else
                    {
                        // 使用 Opus 解码器解码为 PCM
                        // opus_decode(decoder, buf, buf_size, pcm_buf, frame_size, 0)
                        // Concentus 的 IOpusDecoder.Decode 返回解码的样本数
                        // IOpusDecoder 使用 float[] 作为输出缓冲区
                        // frame_size 参数是每声道的样本数
                        float[] pcmBufferFloat = new float[_audioChannels * _audioFrameSize];
                        int samplesDecoded = _opusDecoder.Decode(opusFrame.AsSpan(), pcmBufferFloat.AsSpan(), _audioFrameSize, false);
                        
                        if (samplesDecoded > 0)
                        {
                            // 将 float 样本转换为 short[]，然后转换为字节数组（s16le）
                            short[] pcmBuffer = new short[samplesDecoded * _audioChannels];
                            for (int i = 0; i < samplesDecoded * _audioChannels; i++)
                            {
                                // 将 float (-1.0 到 1.0) 转换为 short (-32768 到 32767)
                                float clamped = Math.Max(-1.0f, Math.Min(1.0f, pcmBufferFloat[i]));
                                pcmBuffer[i] = (short)(clamped * 32767.0f);
                            }
                            byte[] pcmBytes = new byte[samplesDecoded * _audioChannels * 2]; // 每个样本 2 字节
                            System.Buffer.BlockCopy(pcmBuffer, 0, pcmBytes, 0, pcmBytes.Length);
                            
                            _audioWriter.Write(pcmBytes, 0, pcmBytes.Length);
                            
                            if (_audioPacketCount == 0)
                            {
                                _logger?.LogInformation("First audio packet decoded: Opus {OpusSize} bytes -> PCM {PcmSize} bytes ({Samples} samples)", 
                                    opusFrame.Length, pcmBytes.Length, samplesDecoded);
                            }
                        }
                        else
                        {
                            if (_audioPacketCount < 5)
                            {
                                _logger?.LogWarning("Opus decode returned 0 samples for packet {Count}", _audioPacketCount);
                            }
                        }
                    }
                }
                
                if (_audioPacketCount % 10 == 0)
                {
                    try { _audioWriter.Flush(); } catch { }
                }
                
                _audioPacketCount++;
                
                if (_audioPacketCount == 10 || _audioPacketCount == 100)
                {
                    _logger?.LogInformation("Audio packets received: {Count}", _audioPacketCount);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process audio packet");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            // 释放 Opus 解码器
            lock (_opusDecoderLock)
            {
                _opusDecoder?.Dispose();
                _opusDecoder = null;
            }
            try { _videoWriter?.Flush(); } catch { }
            try { _audioWriter?.Flush(); } catch { }
            try { _videoWriter?.Dispose(); } catch { }
            try { _audioWriter?.Dispose(); } catch { }
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { }
            finally
            {
                try { _proc?.Dispose(); } catch { }
            }

            if (_useTcp)
            {
                try { _videoClient?.Dispose(); } catch { }
                try { _audioClient?.Dispose(); } catch { }
            }
            else
            {
                try { if (File.Exists(_fifoVideo)) File.Delete(_fifoVideo); } catch { }
                try { if (_enableAudio && File.Exists(_fifoAudio)) File.Delete(_fifoAudio); } catch { }
                try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true); } catch { }
            }
        }
    }
}



