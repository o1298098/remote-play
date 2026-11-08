using Microsoft.EntityFrameworkCore;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.PlayStation;
using RemotePlay.Models.Context;
using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Receiver;
using RemotePlay.Services.Streaming.Core;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RemotePlay.Hubs;
using Microsoft.Extensions.DependencyInjection;

namespace RemotePlay.Services.Streaming
{
    public class StreamingService : IStreamingService
    {
        private readonly ILogger<StreamingService> _logger;
        private readonly ISessionService _sessionService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _serviceProvider;

        private readonly ConcurrentDictionary<Guid, RPStreamV2> _streams = new();

        public StreamingService(
            ILogger<StreamingService> logger, 
            ISessionService sessionService, 
            ILoggerFactory loggerFactory,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _sessionService = sessionService;
            _loggerFactory = loggerFactory;
            _serviceProvider = serviceProvider;
        }

        public Task<bool> AttachReceiverAsync(Guid sessionId, IAVReceiver receiver, CancellationToken cancellationToken = default)
        {
            if (_streams.TryGetValue(sessionId, out var rp))
            {
                rp.AddReceiver(receiver);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public async Task<bool> StartStreamAsync(Guid sessionId, bool isTest = true, CancellationToken cancellationToken = default)
        {
            var session = await _sessionService.GetSessionAsync(sessionId, cancellationToken);
            if (session == null) return false;
            var rpLogger = _loggerFactory.CreateLogger<Core.RPStreamV2>();
            // PS5 默认使用 9296 作为首选端口
            var port = 9296;
            var ct = cancellationToken;
            var rp = new Core.RPStreamV2(rpLogger, _loggerFactory, session, session.HostIp, port, ct);
            
            // 🔹 如果是测试模式，自动附加默认接收器（用于调试）
            // 注意：WebRTCReceiver 应该通过 WebRTCController 创建，而不是在这里自动创建
            // 因为 WebRTCReceiver 需要经过完整的 WebRTC 信令交换才能工作
            if (isTest)
            {
                var defaultReceiver = new DefaultReceiver(
                    _loggerFactory.CreateLogger<DefaultReceiver>());
                rp.AddReceiver(defaultReceiver);
                _logger.LogInformation("Test mode: Auto-attached DefaultReceiver for session {SessionId}", sessionId);
            }
            
            // 设置断开连接回调
            rp.SetOnDisconnectCallback(async () =>
            {
                await HandleStreamDisconnectAsync(sessionId);
            });
            
            await rp.StartAsync();
            _streams[sessionId] = rp;
            return true;
        }
        
        /// <summary>
        /// 处理流断开连接（由 PS5 主动断开）
        /// </summary>
        private async Task HandleStreamDisconnectAsync(Guid sessionId)
        {
            try
            {
                _logger.LogWarning("Handling stream disconnect for session {SessionId}", sessionId);
                
                // 从流字典中移除（流会在 RPStreamV2.HandleDisconnectAsync 中自己停止）
                _streams.TryRemove(sessionId, out _);
                
                // 停止 session
                await _sessionService.StopSessionAsync(sessionId);
                
                // 通知客户端
                await NotifyClientDisconnectAsync(sessionId);
                
                _logger.LogInformation("Stream disconnect handled for session {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling stream disconnect for session {SessionId}", sessionId);
            }
        }
        
        /// <summary>
        /// 通知客户端断开连接
        /// </summary>
        private async Task NotifyClientDisconnectAsync(Guid sessionId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<DeviceStatusHub>>();
                var dbContext = scope.ServiceProvider.GetRequiredService<RPContext>();

                var session = await _sessionService.GetSessionAsync(sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Cannot notify client: session {SessionId} not found", sessionId);
                    return;
                }

                if (string.IsNullOrEmpty(session.HostId))
                {
                    _logger.LogWarning("Cannot notify client: session {SessionId} has no HostId", sessionId);
                    return;
                }

                var userIds = await dbContext.UserDevices
                    .Where(ud => ud.IsActive
                                 && ud.Device != null
                                 && ud.Device.HostId == session.HostId
                                 && !string.IsNullOrEmpty(ud.UserId))
                    .Select(ud => ud.UserId!)
                    .Distinct()
                    .ToListAsync();

                if (userIds.Count == 0)
                {
                    _logger.LogWarning("No user bound to host {HostId}, skipping disconnect notification: SessionId={SessionId}", session.HostId, sessionId);
                    return;
                }

                var payload = new
                {
                    sessionId = sessionId,
                    reason = "PS5主动断开连接",
                    timestamp = DateTime.UtcNow
                };
                
                foreach (var userId in userIds)
                {
                    var groupName = DeviceStatusHub.GetUserGroupName(userId);
                    await hubContext.Clients.Group(groupName).SendAsync("SessionDisconnected", payload);
                    _logger.LogDebug("Sent disconnect notification to group {GroupName}: SessionId={SessionId}", groupName, sessionId);
                }
                
                _logger.LogInformation("Disconnect notification sent to clients for session {SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify clients about disconnect for session {SessionId}", sessionId);
            }
        }

        public async Task<bool> StopStreamAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (_streams.TryRemove(sessionId, out var rp))
            {
                try 
                { 
                    // ✅ 添加超时机制，避免无限等待
                    var stopTask = rp.StopAsync();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    var completedTask = await Task.WhenAny(stopTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("Stopping stream {SessionId} timed out (5s), continuing", sessionId);
                    }
                    else
                    {
                        await stopTask;
                    }
                } 
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Stopping stream {SessionId} was canceled", sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping stream {SessionId}", sessionId);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 停止所有活动的流（用于应用程序关闭时）
        /// </summary>
        public async Task StopAllStreamsAsync(TimeSpan timeout)
        {
            if (_streams.IsEmpty)
            {
                _logger.LogInformation("No active streams to stop");
                return;
            }

            var streamCount = _streams.Count;
            _logger.LogInformation("Stopping all {Count} active streams...", streamCount);

            // 获取所有流并清空字典
            var streamsToStop = new List<(Guid SessionId, RPStreamV2 Stream)>();
            foreach (var kvp in _streams)
            {
                streamsToStop.Add((kvp.Key, kvp.Value));
            }
            _streams.Clear();

            // 并行停止所有流，但限制并发数
            var semaphore = new SemaphoreSlim(5, 5); // 最多同时停止5个流
            var cts = new CancellationTokenSource(timeout);
            var stopTasks = streamsToStop.Select(async kvp =>
            {
                await semaphore.WaitAsync(cts.Token);
                try
                {
                    _logger.LogDebug("Stopping stream {SessionId}...", kvp.SessionId);
                    
                    // 每个流使用共享的 CancellationToken，确保在总超时时间内完成
                    var stopTask = kvp.Stream.StopAsync();
                    var timeoutTask = Task.Delay(timeout, cts.Token);
                    var completedTask = await Task.WhenAny(stopTask, timeoutTask);
                    
                    if (completedTask == timeoutTask || cts.Token.IsCancellationRequested)
                    {
                        _logger.LogWarning("Stopping stream {SessionId} timed out or was canceled, continuing", kvp.SessionId);
                    }
                    else
                    {
                        await stopTask;
                        _logger.LogDebug("Stream {SessionId} stopped", kvp.SessionId);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Stopping stream {SessionId} was canceled", kvp.SessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping stream {SessionId}", kvp.SessionId);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            // 等待所有停止任务完成，但不超过总超时时间
            try
            {
                await Task.WhenAll(stopTasks).WaitAsync(timeout);
                _logger.LogInformation("All {Count} streams stopped", streamCount);
            }
            catch (TimeoutException)
            {
                cts.Cancel();
                _logger.LogWarning("Stopping all streams timed out ({Timeout}s), continuing shutdown", timeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping streams");
            }
            finally
            {
                cts.Dispose();
                semaphore.Dispose();
            }
        }

        public Task<RPStreamV2?> GetStreamAsync(Guid sessionId)
        {
            _streams.TryGetValue(sessionId, out var stream);
            return Task.FromResult(stream);
        }

        public async Task<RemoteSession?> GetSessionAsync(Guid sessionId)
        {
            return await _sessionService.GetSessionAsync(sessionId);
        }

        public Task<bool> IsStreamRunningAsync(Guid sessionId)
        {
            return Task.FromResult(_streams.ContainsKey(sessionId));
        }

        public Task<bool> ForceResetReorderQueueAsync(Guid sessionId)
        {
            if (_streams.TryGetValue(sessionId, out var stream))
            {
                stream.ForceResetReorderQueue();
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }
}


