using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using System.Linq;

namespace RemotePlay.Middleware
{
    /// <summary>
    /// HLS 路径修复中间件
    /// 只处理 .m3u8 文件，将相对路径转换为绝对路径
    /// 其他文件（.ts）直接由静态文件中间件处理
    /// </summary>
    public class HlsPathFixMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<HlsPathFixMiddleware> _logger;
        
        // 简单的内存缓存（基于文件修改时间）
        // 对于实时更新的 HLS 流，使用很短的缓存时间
        private static readonly ConcurrentDictionary<string, (string content, long lastModifiedTicks, DateTime cachedTime)> _cache = new();
        private static readonly TimeSpan _cacheExpiry = TimeSpan.FromMilliseconds(200); // 缓存 200ms，减少重复的文件读取和检查

        public HlsPathFixMiddleware(
            RequestDelegate next,
            IWebHostEnvironment environment,
            ILogger<HlsPathFixMiddleware> logger)
        {
            _next = next;
            _environment = environment;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";
            
            // 只处理 .m3u8 文件
            if (path.StartsWith("/hls/", StringComparison.OrdinalIgnoreCase) && 
                path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var webRootPath = _environment.WebRootPath ?? _environment.ContentRootPath;
                var filePath = Path.Combine(webRootPath, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(filePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var lastModified = fileInfo.LastWriteTimeUtc;
                        var lastModifiedTicks = lastModified.Ticks;
                        
                        // 检查缓存
                        var cacheKey = filePath;
                        
                        // 对于实时更新的 HLS 流，禁用缓存以确保播放器能看到最新的片段
                        // 实时流需要频繁更新清单，缓存会导致播放器看不到新片段
                        string fixedContent;
                        bool useCache = false;
                        // 暂时禁用缓存，每次都重新处理以确保清单是最新的
                        fixedContent = "";
                        
                        if (!useCache)
                        {
                            // 使用 FileShare.Read 允许并发读取（FFmpeg 可能在写入，但我们仍可以读取）
                            string? m3u8Content = null;
                            const int maxRetries = 3;
                            int retryCount = 0;
                            
                            while (retryCount < maxRetries)
                            {
                                try
                                {
                                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                                    using (var reader = new StreamReader(fileStream, System.Text.Encoding.UTF8))
                                    {
                                        m3u8Content = await reader.ReadToEndAsync();
                                        break; // 成功读取，退出重试循环
                                    }
                                }
                                catch (IOException) when (retryCount < maxRetries - 1)
                                {
                                    // 文件被占用，等待后重试
                                    retryCount++;
                                    await Task.Delay(50); // 等待 50ms 后重试
                                    continue;
                                }
                            }
                            
                            if (m3u8Content == null || retryCount >= maxRetries)
                            {
                                // 重试失败 - 文件可能正在被 FFmpeg 重写
                                // 返回一个有效的空 m3u8 清单，让播放器继续等待
                                _logger.LogDebug("无法读取 HLS 文件，文件可能正在被写入，返回空清单: {Path}", path);
                                
                                var emptyM3u8 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n\n";
                                var emptyEncoding = new System.Text.UTF8Encoding(false);
                                var emptyBytes = emptyEncoding.GetBytes(emptyM3u8);
                                
                                context.Response.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                                context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                                context.Response.Headers["Access-Control-Allow-Headers"] = "*";
                                context.Response.ContentLength = emptyBytes.Length;
                                context.Response.StatusCode = 200; // 返回 200 而不是 503，让播放器继续
                                
                                await context.Response.Body.WriteAsync(emptyBytes.AsMemory(0, emptyBytes.Length));
                                return;
                            }
                            
                            // 构建基础 URL（确保使用正斜杠）
                            var dirPath = path.Substring(0, path.LastIndexOf('/') + 1);
                            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{dirPath}";
                            
                            // 使用更简单的方法处理路径转换，并过滤已删除的 .ts 文件
                            var lines = m3u8Content.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
                            var fixedLines = new System.Collections.Generic.List<string>(lines.Length);
                            var tsDirectory = Path.GetDirectoryName(filePath) ?? "";
                            
                            // 先收集所有有效片段（包含标签和文件路径）
                            var validSegments = new System.Collections.Generic.List<(List<string> prefixLines, string tsFileName, DateTime lastModified)>();
                            var segmentPrefixLines = new System.Collections.Generic.List<string>();
                            int skippedSegments = 0;
                            
                            for (int i = 0; i < lines.Length; i++)
                            {
                                var line = lines[i];
                                var trimmedLine = line.Trim();
                                
                                // 空行处理
                                if (string.IsNullOrEmpty(trimmedLine))
                                {
                                    continue;
                                }
                                
                                // 检查是否是 .ts 文件名行
                                if (trimmedLine.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                                    !trimmedLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                    !trimmedLine.StartsWith("/"))
                                {
                                    // 检查 .ts 文件是否存在（对于实时流，文件可能正在被创建或已删除）
                                    var tsFilePath = Path.Combine(tsDirectory, trimmedLine);
                                    try
                                    {
                                        // 使用 FileShare.Read 检查文件是否存在且可读
                                        if (File.Exists(tsFilePath))
                                        {
                                            var tsFileInfo = new FileInfo(tsFilePath);
                                            // 尝试打开文件确认可读（避免文件正在写入中）
                                            using (var testStream = new FileStream(tsFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
                                            {
                                                // 文件存在且可读，保存片段信息（包含标签和文件路径）
                                                validSegments.Add((
                                                    new System.Collections.Generic.List<string>(segmentPrefixLines),
                                                    trimmedLine,
                                                    tsFileInfo.LastWriteTimeUtc
                                                ));
                                            }
                                        }
                                        else
                                        {
                                            skippedSegments++;
                                        }
                                    }
                                    catch (IOException)
                                    {
                                        // 文件正在被写入或被锁定，跳过这个片段
                                        skippedSegments++;
                                        _logger.LogDebug("跳过正在写入的 .ts 文件: {TsFile}", trimmedLine);
                                    }
                                    // 清空累积的标签行
                                    segmentPrefixLines.Clear();
                                }
                                else if (trimmedLine.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) ||
                                         trimmedLine.StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.OrdinalIgnoreCase))
                                {
                                    // 片段相关的标签，累积起来等待 .ts 文件名行
                                    segmentPrefixLines.Add(line);
                                }
                            }
                            
                            // 只保留最后几个片段（避免播放器请求已删除的文件）
                            // 对于实时流，只保留最近 5-10 个片段更安全
                            const int maxSegmentsToKeep = 8;
                            if (validSegments.Count > maxSegmentsToKeep)
                            {
                                // 按文件名中的数字排序（HLS 片段是按序列号命名的，如 seg_00000.ts, seg_00001.ts）
                                // 提取文件名中的数字进行排序，保留最大的几个
                                validSegments = validSegments
                                    .OrderByDescending(s =>
                                    {
                                        // 从文件名中提取数字，如 "seg_00018.ts" -> 18
                                        var match = System.Text.RegularExpressions.Regex.Match(s.tsFileName, @"\d+");
                                        return match.Success ? int.Parse(match.Value) : 0;
                                    })
                                    .Take(maxSegmentsToKeep)
                                    .OrderBy(s =>
                                    {
                                        // 再按数字正序排列，保持播放顺序
                                        var match = System.Text.RegularExpressions.Regex.Match(s.tsFileName, @"\d+");
                                        return match.Success ? int.Parse(match.Value) : 0;
                                    })
                                    .ToList();
                            }
                            else
                            {
                                // 即使片段数量不多，也按文件名数字排序
                                validSegments = validSegments
                                    .OrderBy(s =>
                                    {
                                        var match = System.Text.RegularExpressions.Regex.Match(s.tsFileName, @"\d+");
                                        return match.Success ? int.Parse(match.Value) : 0;
                                    })
                                    .ToList();
                            }
                            
                            // 现在构建最终的清单内容
                            // 先添加所有非片段的标签行
                            for (int i = 0; i < lines.Length; i++)
                            {
                                var line = lines[i];
                                var trimmedLine = line.Trim();
                                
                                // 跳过空行
                                if (string.IsNullOrEmpty(trimmedLine))
                                {
                                    continue;
                                }
                                
                                // 跳过片段相关的行（EXTINF、EXT-X-PROGRAM-DATE-TIME、.ts 文件名）
                                if (trimmedLine.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) ||
                                    trimmedLine.StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.OrdinalIgnoreCase) ||
                                    (trimmedLine.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                                     !trimmedLine.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                     !trimmedLine.StartsWith("/")))
                                {
                                    continue;
                                }
                                
                                // 添加其他标签（如 #EXTM3U、#EXT-X-VERSION、#EXT-X-TARGETDURATION 等）
                                fixedLines.Add(line);
                            }
                            
                            // 添加保留的片段（标签 + URL）
                            foreach (var segment in validSegments)
                            {
                                foreach (var prefixLine in segment.prefixLines)
                                {
                                    fixedLines.Add(prefixLine);
                                }
                                fixedLines.Add(baseUrl + segment.tsFileName);
                            }
                            
                            // 确保最后有换行符（HLS 规范要求）
                            fixedContent = string.Join("\n", fixedLines);
                            if (!fixedContent.EndsWith("\n"))
                            {
                                fixedContent += "\n";
                            }
                            
                            // 更新缓存（使用最新的文件修改时间）
                            _cache.AddOrUpdate(cacheKey, 
                                (fixedContent, lastModifiedTicks, DateTime.UtcNow),
                                (key, oldValue) => (fixedContent, lastModifiedTicks, DateTime.UtcNow));
                            
                            // 记录转换统计
                            if (validSegments.Count > 0 || skippedSegments > 0)
                            {
                                _logger.LogDebug("HLS m3u8 路径转换完成: {Path}, 保留片段: {Valid}, 跳过片段: {Skipped}", 
                                    path, validSegments.Count, skippedSegments);
                            }
                        }

                        // 使用 UTF-8 编码，不带 BOM
                        var utf8NoBom = new System.Text.UTF8Encoding(false);
                        var fixedBytes = utf8NoBom.GetBytes(fixedContent);

                        // 对于实时 HLS 流，基于处理后的内容生成 ETag（而不是文件修改时间）
                        // 这样即使文件修改时间相同，如果片段列表不同，ETag 也不同
                        // 使用内容的哈希值作为 ETag
                        using (var sha256 = System.Security.Cryptography.SHA256.Create())
                        {
                            var hashBytes = sha256.ComputeHash(fixedBytes);
                            var hashString = Convert.ToBase64String(hashBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                            var etag = $"\"{hashString.Substring(0, Math.Min(16, hashString.Length))}\"";

                            // 检查 If-None-Match (ETag) - 只有当内容完全相同时才返回 304
                            var ifNoneMatch = context.Request.Headers.IfNoneMatch;
                            if (ifNoneMatch.Count > 0)
                            {
                                foreach (var tag in ifNoneMatch)
                                {
                                    if (tag == etag || tag == "*")
                                    {
                                        context.Response.StatusCode = 304;
                                        context.Response.Headers.ETag = etag;
                                        context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                                        return;
                                    }
                                }
                            }

                            // 设置响应头
                            context.Response.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                        context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                        context.Response.Headers.ETag = etag;
                        }
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
                        context.Response.ContentLength = fixedBytes.Length;

                        await context.Response.Body.WriteAsync(fixedBytes.AsMemory(0, fixedBytes.Length));
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理 HLS m3u8 文件时发生错误: {Path}", path);
                        // 发生错误时，返回 500 而不是继续传递请求
                        context.Response.StatusCode = 500;
                        return;
                    }
                }
                else
                {
                    // 文件不存在 - 对于实时 HLS 流，可能是文件正在被 FFmpeg 重写
                    // 检查目录是否存在（如果目录存在，说明流还在进行中）
                    var dirPath = Path.GetDirectoryName(filePath);
                    if (dirPath != null && Directory.Exists(dirPath))
                    {
                        // 目录存在，说明流还在，返回一个有效的空 m3u8 清单
                        // 这样播放器会继续等待，而不是停止播放
                        _logger.LogDebug("HLS m3u8 文件暂时不存在，但目录存在，返回空清单: {Path}", path);
                        
                        var emptyM3u8 = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n\n";
                        var emptyEncoding = new System.Text.UTF8Encoding(false);
                        var emptyBytes = emptyEncoding.GetBytes(emptyM3u8);
                        
                        context.Response.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
                        context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
                        context.Response.ContentLength = emptyBytes.Length;
                        context.Response.StatusCode = 200; // 返回 200 而不是 404，让播放器继续
                        
                        await context.Response.Body.WriteAsync(emptyBytes.AsMemory(0, emptyBytes.Length));
                        return;
                    }
                    else
                    {
                        // 目录也不存在，说明流确实已经结束了，返回 404
                        _logger.LogDebug("HLS m3u8 文件和目录都不存在，返回 404: {Path}", path);
                        context.Response.StatusCode = 404;
                        return;
                    }
                }
            }

            // 其他请求继续传递
            await _next(context);
        }
    }
}
