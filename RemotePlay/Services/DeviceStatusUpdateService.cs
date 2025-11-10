using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Context;
using RemotePlay.Models.DB.PlayStation;
using RemotePlay.Models.PlayStation;
using RemotePlay.Hubs;
using System.Diagnostics;

namespace RemotePlay.Services
{
    /// <summary>
    /// 设备状态更新服务
    /// 定期扫描数据库中的设备并更新其状态信息
    /// </summary>
    public class DeviceStatusUpdateService : BackgroundService
    {
        private readonly ILogger<DeviceStatusUpdateService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _updateInterval;
        private readonly int _discoveryTimeoutMs;
        private readonly int _batchSize;

        public DeviceStatusUpdateService(
            ILogger<DeviceStatusUpdateService> logger,
            IServiceProvider serviceProvider,
            IOptions<DeviceStatusUpdateConfig> config)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _updateInterval = TimeSpan.FromSeconds(config.Value.UpdateIntervalSeconds);
            _discoveryTimeoutMs = config.Value.DiscoveryTimeoutMs;
            _batchSize = config.Value.BatchSize;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("设备状态更新服务已启动，更新间隔: {Interval}秒", _updateInterval.TotalSeconds);

            // 等待应用完全启动后再开始第一次更新
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await UpdateDeviceStatusesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "更新设备状态时发生错误");
                }

                // 等待下一次更新
                try
                {
                    await Task.Delay(_updateInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("设备状态更新服务已停止");
        }

        /// <summary>
        /// 更新所有设备的状态
        /// </summary>
        private async Task UpdateDeviceStatusesAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var updatedCount = 0;
            // 按状态统计
            var statusCounts = new Dictionary<string, int>
            {
                { "OK", 0 },
                { "STANDBY", 0 },
                { "OFFLINE", 0 },
                { "ERROR", 0 }
            };

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<RPContext>();
                var discoveryService = scope.ServiceProvider.GetRequiredService<IDeviceDiscoveryService>();

                // 获取所有已注册且有HostId的设备
                var devices = await context.PSDevices
                    .Where(d => !string.IsNullOrEmpty(d.HostId) && d.IsRegistered == true)
                    .ToListAsync(cancellationToken);

                if (devices.Count == 0)
                {
                    return;
                }

                _logger.LogInformation("开始更新 {Count} 个设备的状态", devices.Count);

                // 发现网络中的所有在线设备
                var discoveredDevices = await discoveryService.DiscoverDevicesAsync(
                    _discoveryTimeoutMs,
                    cancellationToken);

                // 创建 hostid 到 ConsoleInfo 的映射字典
                var discoveredDevicesMap = discoveredDevices
                    .Where(d => !string.IsNullOrEmpty(d.Uuid))
                    .ToDictionary(d => d.Uuid, d => d);

                // 通过 hostid 匹配并更新设备
                foreach (var device in devices)
                {
                    try
                    {
                        var result = await UpdateDeviceStatusByHostIdAsync(
                            device,
                            discoveredDevicesMap,
                            context,
                            cancellationToken);

                        updatedCount++;
                        
                        // 按设备状态统计
                        var status = device.Status ?? "OFFLINE";
                        var normalizedStatus = NormalizeStatusForCount(status);
                        if (statusCounts.ContainsKey(normalizedStatus))
                        {
                            statusCounts[normalizedStatus]++;
                        }
                        else
                        {
                            // 如果状态不在预定义列表中，添加到字典中
                            if (!statusCounts.ContainsKey(status))
                            {
                                statusCounts[status] = 0;
                            }
                            statusCounts[status]++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "更新设备 {DeviceId} (HostId: {HostId}) 状态时发生错误", device.Id, device.HostId);
                        device.Status = "ERROR";
                        statusCounts["ERROR"]++;
                        updatedCount++;
                    }
                }

                // 批量保存更改
                try
                {
                    await context.SaveChangesAsync(cancellationToken);

                    // 通过SignalR推送设备状态更新通知
                    // 只推送通过UserDevice关联的已注册设备
                    try
                    {
                        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<DeviceStatusHub>>();
                        
                        // 通过UserDevice查询所有已更新的已注册设备（只查询用户绑定的设备）
                        var deviceIds = devices.Select(d => d.Id).ToList();
                        
                        var updatedUserDevices = await context.UserDevices
                            .Where(ud => deviceIds.Contains(ud.DeviceId) 
                                && ud.IsActive 
                                && ud.Device != null 
                                && ud.Device.IsRegistered == true
                                && !string.IsNullOrEmpty(ud.Device.HostId))
                            .Include(ud => ud.Device)
                            .Select(ud => new
                            {
                                UserId = ud.UserId,
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
                            .ToListAsync(cancellationToken);
                        
                        if (updatedUserDevices.Count > 0)
                        {
                            var userGroups = updatedUserDevices
                                .Where(ud => !string.IsNullOrEmpty(ud.UserId))
                                .GroupBy(ud => ud.UserId!)
                                .ToList();

                            foreach (var userGroup in userGroups)
                            {
                                var payload = userGroup.Select(ud => new
                                {
                                    ud.UserDeviceId,
                                    ud.DeviceId,
                                    ud.HostId,
                                    ud.HostName,
                                    ud.HostType,
                                    ud.IpAddress,
                                    ud.SystemVersion,
                                    ud.IsRegistered,
                                    ud.Status,
                                    ud.LastUsedAt,
                                    ud.CreatedAt
                                }).ToList();

                                var groupName = DeviceStatusHub.GetUserGroupName(userGroup.Key);
                                await hubContext.Clients.Group(groupName)
                                    .SendAsync("DeviceStatusUpdated", payload);
                            }
                        }
                    }
                    catch (Exception hubEx)
                    {
                        _logger.LogWarning(hubEx, "推送设备状态更新通知时出错（不影响主流程）");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存设备状态更新时发生错误");
                }

                stopwatch.Stop();
                
                // 构建状态统计日志
                var statusSummary = string.Join(", ", statusCounts
                    .Where(kvp => kvp.Value > 0)
                    .Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新设备状态过程中发生错误");
            }
        }

        /// <summary>
        /// 通过 hostid 匹配并更新单个设备的状态
        /// </summary>
        private async Task<UpdateResult> UpdateDeviceStatusByHostIdAsync(
            Device device,
            Dictionary<string, ConsoleInfo> discoveredDevicesMap,
            RPContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(device.HostId))
                {
                    _logger.LogWarning("设备 {DeviceId} 没有 HostId，跳过更新", device.Id);
                    return new UpdateResult { HasError = true };
                }

                // 通过 hostid 在发现的设备中查找匹配的设备
                if (discoveredDevicesMap.TryGetValue(device.HostId, out var consoleInfo))
                {
                    // 设备在线，更新信息
                    // 标准化状态值：OK、STANDBY、OFFLINE
                    device.Status = NormalizeDeviceStatus(consoleInfo.status);
                    device.HostName = consoleInfo.Name;
                    device.HostType = consoleInfo.HostType ?? device.HostType;
                    device.SystemVersion = consoleInfo.SystemVerion ?? device.SystemVersion;
                    device.DiscoverProtocolVersion = consoleInfo.DeviceDiscoverPotocolVersion ?? device.DiscoverProtocolVersion;

                    // 更新IP地址（IP地址可能已变化）
                    if (consoleInfo.Ip != device.IpAddress)
                    {
                        _logger.LogInformation(
                            "设备 {DeviceId} (HostId: {HostId}) 的IP地址从 {OldIp} 更新为 {NewIp}",
                            device.Id, device.HostId, device.IpAddress, consoleInfo.Ip);
                        device.IpAddress = consoleInfo.Ip;
                    }

                    return new UpdateResult { IsOnline = true };
                }
                else
                {
                    // 设备不在发现的设备列表中，标记为离线
                    device.Status = "OFFLINE";
                    return new UpdateResult { IsOnline = false };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新设备 {DeviceId} (HostId: {HostId}) 状态时发生错误", device.Id, device.HostId);
                device.Status = "ERROR";
                return new UpdateResult { HasError = true };
            }
        }

        /// <summary>
        /// 标准化设备状态值
        /// 将设备返回的状态值映射为：OK、STANDBY、OFFLINE
        /// </summary>
        private string NormalizeDeviceStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "OFFLINE"; // 默认状态为离线
            }

            var statusUpper = status.ToUpperInvariant();

            // 检查是否包含 STANDBY 相关关键词
            if (statusUpper.Contains("STANDBY"))
            {
                return "STANDBY";
            }

            // 检查是否包含 OK 相关关键词（如 "200 OK"）
            if (statusUpper.Contains("OK"))
            {
                return "OK";
            }

            // 默认返回原值（如果无法识别，返回原值）
            return status;
        }

        /// <summary>
        /// 标准化状态用于统计
        /// 将各种状态值统一为统计用的标准状态
        /// </summary>
        private string NormalizeStatusForCount(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "OFFLINE";
            }

            var statusUpper = status.ToUpperInvariant();

            // 检查是否包含 STANDBY 相关关键词
            if (statusUpper.Contains("STANDBY"))
            {
                return "STANDBY";
            }

            // 检查是否包含 OK 相关关键词（如 "200 OK"）
            if (statusUpper.Contains("OK"))
            {
                return "OK";
            }

            // 检查是否包含 ERROR 相关关键词
            if (statusUpper.Contains("ERROR") || statusUpper.Contains("ERR"))
            {
                return "ERROR";
            }

            // 检查是否包含 OFFLINE 相关关键词
            if (statusUpper.Contains("OFFLINE") || statusUpper.Contains("OFF"))
            {
                return "OFFLINE";
            }

            // 如果无法识别，返回原值（大写）
            return statusUpper;
        }

        /// <summary>
        /// 更新结果
        /// </summary>
        private class UpdateResult
        {
            public bool IsOnline { get; set; }
            public bool HasError { get; set; }
        }

        public override void Dispose()
        {
            base.Dispose();
        }
    }

    /// <summary>
    /// 设备状态更新配置
    /// </summary>
    public class DeviceStatusUpdateConfig
    {
        /// <summary>
        /// 更新间隔（秒）
        /// </summary>
        public int UpdateIntervalSeconds { get; set; } = 300; // 默认5分钟

        /// <summary>
        /// 设备发现超时时间（毫秒）
        /// </summary>
        public int DiscoveryTimeoutMs { get; set; } = 2000;

        /// <summary>
        /// 批处理大小
        /// </summary>
        public int BatchSize { get; set; } = 10;
    }
}

