using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RemotePlay.Services.Streaming.Core
{
    /// <summary>
    /// RPStreamV2 - Pipeline 统计监控扩展
    /// </summary>
    public sealed partial class RPStreamV2
    {
        #region Pipeline Statistics Monitoring
        
        /// <summary>
        /// 启动 Pipeline 统计监控
        /// </summary>
        private void StartPipelineStatsMonitoring()
        {
            if (_avPipeline == null)
                return;
                
            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                try
                {
                    while (await timer.WaitForNextTickAsync(_cancellationToken))
                    {
                        if (_avPipeline == null)
                            break;

                        var stats = _avPipeline.GetStats();
                        
                        // 输出基本统计
                        _logger.LogDebug(
                            "📊 Pipeline: Ingest={In}, Video={Vid}, Audio={Aud}, Output={Out}",
                            $"R:{stats.Ingest.TotalReceived}/P:{stats.Ingest.TotalParsed}",
                            $"R:{stats.Video.TotalReceived}/C:{stats.Video.FramesComplete}/D:{stats.Video.TotalDropped}",
                            $"R:{stats.Audio.TotalReceived}/C:{stats.Audio.FramesComplete}",
                            $"VS:{stats.Output.VideoFramesSent}/AS:{stats.Output.AudioFramesSent}"
                        );
                        
                        // 检测瓶颈
                        if (stats.Ingest.InputQueueSize > 1000)
                        {
                            _logger.LogWarning("⚠️ Ingest 积压: {Size} 个包", stats.Ingest.InputQueueSize);
                        }
                        
                        if (stats.Video.ReorderBufferSize > 150)
                        {
                            _logger.LogWarning("⚠️ Video ReorderQueue 积压: {Size} 个包", stats.Video.ReorderBufferSize);
                        }
                        
                        if (stats.Output.VideoQueueSize > 100)
                        {
                            _logger.LogWarning("⚠️ Output Video 积压: {Size} 帧", stats.Output.VideoQueueSize);
                        }
                        
                        // 检查丢包率
                        if (stats.Video.TotalReceived > 0)
                        {
                            var dropRate = (double)((long)stats.Video.TotalDropped + (long)stats.Video.ReorderDropped) / (double)stats.Video.TotalReceived * 100;
                            if (dropRate > 5)
                            {
                                _logger.LogWarning(
                                    "⚠️ 视频丢包率: {DropRate:F2}% ({Dropped}/{Total})",
                                    dropRate,
                                    (long)stats.Video.TotalDropped + (long)stats.Video.ReorderDropped,
                                    stats.Video.TotalReceived
                                );
                            }
                        }
                        
                        // 检查解析错误
                        if (stats.Ingest.ParseErrors > 10)
                        {
                            _logger.LogWarning("⚠️ 解析错误: {Errors} 个包", stats.Ingest.ParseErrors);
                        }
                        
                        // 检查解密错误
                        if (stats.Ingest.DecryptErrors > 5)
                        {
                            _logger.LogWarning("⚠️ 解密错误: {Errors} 个包", stats.Ingest.DecryptErrors);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("📊 Pipeline stats monitoring stopped");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Pipeline stats monitoring error");
                }
                finally
                {
                    timer.Dispose();
                }
            }, _cancellationToken);
        }
        
        #endregion
    }
}

