using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RemotePlay.Models.Context;
using System.Security.Claims;

namespace RemotePlay.Hubs
{
    /// <summary>
    /// SignalR Hub for device status updates
    /// 用于设备状态更新的SignalR Hub
    /// </summary>
    public class DeviceStatusHub : Hub
    {
        private const string UserGroupPrefix = "user:";
        private readonly ILogger<DeviceStatusHub> _logger;
        private readonly IServiceProvider _serviceProvider;

        public DeviceStatusHub(ILogger<DeviceStatusHub> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 客户端连接时调用
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            _logger.LogInformation("New DeviceStatusHub connection: ConnectionId={ConnectionId}, UserId={UserId}, IsAuthenticated={IsAuthenticated}", 
                Context.ConnectionId, userId ?? "unauthenticated", Context.User?.Identity?.IsAuthenticated ?? false);

            if (!string.IsNullOrEmpty(userId))
            {
                var groupName = GetUserGroupName(userId);
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                _logger.LogDebug("Connection {ConnectionId} joined user group {GroupName}", Context.ConnectionId, groupName);
            }
            else
            {
                _logger.LogWarning("Cannot add unauthenticated connection {ConnectionId} to user group", Context.ConnectionId);
            }
            
            // 连接时立即发送当前已注册设备的状态
            // 如果用户未认证，SendRegisteredDevicesStatusAsync 会发送错误消息
            await SendRegisteredDevicesStatusAsync();
            
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 客户端断开连接时调用
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception != null)
            {
                _logger.LogWarning(exception, "DeviceStatusHub connection closed: ConnectionId={ConnectionId}", Context.ConnectionId);
            }
            else
            {
                _logger.LogInformation("DeviceStatusHub connection closed: ConnectionId={ConnectionId}", Context.ConnectionId);
            }

            var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                var groupName = GetUserGroupName(userId);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
                _logger.LogDebug("Connection {ConnectionId} removed from user group {GroupName}", Context.ConnectionId, groupName);
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// 获取当前已注册设备的状态
        /// </summary>
        public async Task GetRegisteredDevicesStatus()
        {
            await SendRegisteredDevicesStatusAsync();
        }

        /// <summary>
        /// 发送当前已注册设备的状态给调用者
        /// 只返回通过UserDevice关联的用户设备
        /// </summary>
        private async Task SendRegisteredDevicesStatusAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RPContext>();

                // 获取当前用户ID（必须已认证）
                var userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (string.IsNullOrEmpty(userId))
                {
                    _logger.LogWarning("Unauthenticated user attempted to get device status: ConnectionId={ConnectionId}", Context.ConnectionId);
                    await Clients.Caller.SendAsync("Error", new { message = "需要登录后才能获取设备状态" });
                    return;
                }

                // 通过UserDevice查询用户绑定的已注册设备
                var userDevices = await context.UserDevices
                    .Where(ud => ud.UserId == userId 
                        && ud.IsActive 
                        && ud.Device != null 
                        && ud.Device.IsRegistered == true
                        && !string.IsNullOrEmpty(ud.Device.HostId))
                    .Include(ud => ud.Device)
                    .Select(ud => new
                    {
                        UserDeviceId = ud.Id,
                        DeviceId = ud.Device!.Id,
                        HostId = ud.Device.HostId,
                        HostName = ud.DeviceName ?? ud.Device.HostName,
                        HostType = ud.DeviceType ?? ud.Device.HostType,
                        IpAddress = ud.Device.IpAddress,
                        SystemVersion = ud.Device.SystemVersion,
                        IsRegistered = ud.Device.IsRegistered ?? false,
                        Status = ud.Device.Status ?? "OFFLINE",
                        LastUsedAt = ud.LastUsedAt,
                        CreatedAt = ud.CreatedAt
                    })
                    .ToListAsync();

                await Clients.Caller.SendAsync("RegisteredDevicesStatus", userDevices);

                _logger.LogDebug("Sent registered device status for user {UserId} to client {ConnectionId}, {Count} device(s)", 
                    userId, Context.ConnectionId, userDevices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting registered device status");
                await Clients.Caller.SendAsync("Error", new { message = "获取设备状态失败" });
            }
        }

        public static string GetUserGroupName(string userId) => $"{UserGroupPrefix}{userId}";
    }
}

