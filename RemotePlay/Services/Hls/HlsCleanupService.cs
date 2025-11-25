using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace RemotePlay.Services.Hls
{
    /// <summary>
    /// HLS 文件清理服务
    /// 定期清理 wwwroot/hls 目录中超过保留时间的旧文件
    /// </summary>
    public class HlsCleanupService : BackgroundService
    {
        private readonly ILogger<HlsCleanupService> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5); // 每 5 分钟清理一次
        private readonly TimeSpan _fileRetentionTime = TimeSpan.FromMinutes(10); // 文件保留 10 分钟

        public HlsCleanupService(
            ILogger<HlsCleanupService> logger,
            IWebHostEnvironment environment)
        {
            _logger = logger;
            _environment = environment;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HLS 清理服务已启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOldHlsFilesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "清理 HLS 文件时发生错误");
                }

                // 等待下一次清理
                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("HLS 清理服务已停止");
        }

        /// <summary>
        /// 清理旧的 HLS 文件
        /// </summary>
        private async Task CleanupOldHlsFilesAsync()
        {
            var hlsDirectory = Path.Combine(_environment.WebRootPath ?? _environment.ContentRootPath, "hls");
            
            if (!Directory.Exists(hlsDirectory))
            {
                return;
            }

            var cutoffTime = DateTime.UtcNow - _fileRetentionTime;
            var deletedDirs = 0;
            var deletedFiles = 0;
            long freedSpace = 0;

            try
            {
                // 获取所有子目录（每个会话一个目录）
                var sessionDirectories = Directory.GetDirectories(hlsDirectory);

                foreach (var sessionDir in sessionDirectories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(sessionDir);
                        
                        // 检查目录中所有文件的最后修改时间
                        var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
                        var latestFileTime = files.Length > 0 
                            ? files.Max(f => f.LastWriteTimeUtc) 
                            : dirInfo.LastWriteTimeUtc;

                        // 如果目录中最新的文件也超过保留时间，则删除整个目录
                        if (latestFileTime < cutoffTime)
                        {
                            long dirSize = files.Sum(f => f.Length);
                            
                            Directory.Delete(sessionDir, recursive: true);
                            deletedDirs++;
                            deletedFiles += files.Length;
                            freedSpace += dirSize;

                            _logger.LogDebug(
                                "已删除旧的 HLS 目录: {Directory}, 文件数: {FileCount}, 大小: {Size} MB",
                                Path.GetFileName(sessionDir),
                                files.Length,
                                dirSize / (1024.0 * 1024.0));
                        }
                        else
                        {
                            // 只删除目录中超过保留时间的单个文件（保留目录结构）
                            foreach (var file in files)
                            {
                                if (file.LastWriteTimeUtc < cutoffTime)
                                {
                                    try
                                    {
                                        long fileSize = file.Length;
                                        file.Delete();
                                        deletedFiles++;
                                        freedSpace += fileSize;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "无法删除文件: {File}", file.FullName);
                                    }
                                }
                            }

                            // 如果目录为空，删除目录
                            if (!Directory.EnumerateFileSystemEntries(sessionDir).Any())
                            {
                                try
                                {
                                    Directory.Delete(sessionDir);
                                    deletedDirs++;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "无法删除空目录: {Directory}", sessionDir);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "处理目录时出错: {Directory}", sessionDir);
                    }
                }

                if (deletedDirs > 0 || deletedFiles > 0)
                {
                    _logger.LogInformation(
                        "HLS 清理完成: 删除 {DirCount} 个目录, {FileCount} 个文件, 释放 {Size:F2} MB",
                        deletedDirs,
                        deletedFiles,
                        freedSpace / (1024.0 * 1024.0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理 HLS 目录时发生错误: {Directory}", hlsDirectory);
            }

            await Task.CompletedTask;
        }

        public override void Dispose()
        {
            _logger.LogInformation("HLS 清理服务正在释放资源");
            base.Dispose();
        }
    }
}
