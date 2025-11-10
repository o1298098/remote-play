using RemotePlay.Models.PlayStation;
using System.Diagnostics;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed class FfplayVideoReceiver : IAVReceiver, IDisposable
    {
        private readonly Process _proc;
        private readonly Stream _stdin;
        private bool _disposed;

        public FfplayVideoReceiver(string? ffplayPath = null, string? extraArgs = null)
        {
            var exe = string.IsNullOrWhiteSpace(ffplayPath) ? "ffplay" : ffplayPath!;
            // 通过 stdin 接收 H264 裸流
            // -f h264 明确输入格式；-fflags nobuffer/low_delay 降低延迟；-autoexit 在输入结束退出
            var args = $"-hide_banner -loglevel error -fflags nobuffer -flags low_delay -framedrop -f h264 -i pipe:0 -autoexit {extraArgs}".Trim();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                if (!_proc.Start()) throw new InvalidOperationException("无法启动 ffplay 进程");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("启动 ffplay 失败，请确认已安装 ffmpeg/ffplay 并在 PATH 中", ex);
            }

            _stdin = _proc.StandardInput.BaseStream;
        }

        public void OnStreamInfo(byte[] videoHeader, byte[] audioHeader)
        {
            // 抢先写入视频头（如 SPS/PPS），音频忽略
            if (videoHeader != null && videoHeader.Length > 0)
            {
                _stdin.Write(videoHeader, 0, videoHeader.Length);
                _stdin.Flush();
            }
        }

        public void OnVideoPacket(byte[] packet)
        {
            if (_disposed || packet == null || packet.Length <= 1) return;
            // 丢弃首字节类型标记（VIDEO=0x02）
            _stdin.Write(packet, 1, packet.Length - 1);
            _stdin.Flush();
        }

        public void OnAudioPacket(byte[] packet)
        {
            // 当前示例不处理音频
        }

        public void SetVideoCodec(string codec)
        {
            // FfplayVideoReceiver 暂时只支持 H264，如果需要可以扩展
        }

        public void SetAudioCodec(string codec)
        {
            // FfplayVideoReceiver 仅处理视频，音频编码格式不影响
        }

        public void EnterWaitForIdr()
        {
            // FfplayVideoReceiver 不需要等待 IDR 帧，ffplay 会自动处理
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stdin.Flush(); } catch { }
            try { _stdin.Dispose(); } catch { }
            try
            {
                if (!_proc.HasExited)
                {
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { }
            finally
            {
                try { _proc.Dispose(); } catch { }
            }
        }
    }
}


