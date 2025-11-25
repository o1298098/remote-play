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
                    _logger.LogWarning("无法通知客户端：未找到会话 {SessionId}", sessionId);
                    return;
                }

                if (string.IsNullOrEmpty(session.HostId))
                {
                    _logger.LogWarning("无法通知客户端：会话 {SessionId} 的 HostId 为空", sessionId);
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
                    _logger.LogWarning("未找到绑定主机 {HostId} 的用户，跳过断开通知：SessionId={SessionId}", session.HostId, sessionId);
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
                    _logger.LogDebug("已向用户组 {GroupName} 发送断开通知：SessionId={SessionId}", groupName, sessionId);
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
                try { await rp.StopAsync(); } catch { }
                return true;
            }
            return false;
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
    }
}


