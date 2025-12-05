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

namespace RemotePlay.Services.Device
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
        private readonly int _maxMissedScans;
        private readonly int _retryAttempts;
        private readonly int _retryDelayMs;
        
        // 记录设备连续未发现的次数：Dictionary<DeviceId, MissedCount>
        private readonly Dictionary<string, int> _missedScans = new();
        private readonly object _missedScansLock = new();

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
            _maxMissedScans = config.Value.MaxMissedScansBeforeOffline;
            _retryAttempts = config.Value.RetryAttempts;
            _retryDelayMs = config.Value.RetryDelayMs;
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
                            discoveryService,
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
            Models.DB.PlayStation.Device device,
            Dictionary<string, ConsoleInfo> discoveredDevicesMap,
            RPContext context,
            IDeviceDiscoveryService discoveryService,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(device.HostId))
                {
                    _logger.LogWarning("设备 {DeviceId} 没有 HostId，跳过更新", device.Id);
                    return new UpdateResult { HasError = true };
                }

                ConsoleInfo? consoleInfo = null;
                
                // 首先在广播扫描结果中查找
                if (discoveredDevicesMap.TryGetValue(device.HostId, out consoleInfo))
                {
                    // 设备在广播扫描中被发现
                    _logger.LogDebug("设备 {DeviceId} (HostId: {HostId}) 在广播扫描中被发现", device.Id, device.HostId);
                }
                else
                {
                    // 如果广播扫描未发现，且设备有IP地址，进行重试
                    if (!string.IsNullOrEmpty(device.IpAddress) && _retryAttempts > 0)
                    {
                        _logger.LogDebug(
                            "设备 {DeviceId} (HostId: {HostId}, IP: {IpAddress}) 未在广播扫描中发现，开始单独重试",
                            device.Id, device.HostId, device.IpAddress);

                        // 对特定IP进行重试
                        for (int attempt = 1; attempt <= _retryAttempts; attempt++)
                        {
                            try
                            {
                                if (attempt > 1 && _retryDelayMs > 0)
                                {
                                    await Task.Delay(_retryDelayMs, cancellationToken);
                                }

                                consoleInfo = await discoveryService.DiscoverDeviceAsync(
                                    device.IpAddress,
                                    _discoveryTimeoutMs,
                                    cancellationToken);

                                if (consoleInfo != null && consoleInfo.Uuid == device.HostId)
                                {
                                    _logger.LogInformation(
                                        "设备 {DeviceId} (HostId: {HostId}) 在第 {Attempt} 次重试中被发现",
                                        device.Id, device.HostId, attempt);
                                    break;
                                }
                            }
                            catch (Exception retryEx)
                            {
                                _logger.LogDebug(retryEx, 
                                    "设备 {DeviceId} (HostId: {HostId}) 第 {Attempt} 次重试失败",
                                    device.Id, device.HostId, attempt);
                            }
                        }
                    }
                }

                // 处理发现结果
                if (consoleInfo != null)
                {
                    // 设备在线，重置错过计数
                    lock (_missedScansLock)
                    {
                        _missedScans[device.Id] = 0;
                    }

                    // 更新设备信息
                    device.Status = NormalizeDeviceStatus(consoleInfo.status);
                    device.HostName = consoleInfo.Name;
                    device.HostType = consoleInfo.HostType ?? device.HostType;
                    device.SystemVersion = consoleInfo.SystemVerion ?? device.SystemVersion;
                    device.DiscoverProtocolVersion = consoleInfo.DeviceDiscoverPotocolVersion ?? device.DiscoverProtocolVersion;
                    device.LastSeenAt = DateTime.UtcNow;

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
                    // 设备未发现，增加错过计数
                    int missedCount;
                    lock (_missedScansLock)
                    {
                        if (!_missedScans.ContainsKey(device.Id))
                        {
                            _missedScans[device.Id] = 0;
                        }
                        _missedScans[device.Id]++;
                        missedCount = _missedScans[device.Id];
                    }

                    // 只有连续错过次数超过阈值才标记为离线
                    if (missedCount >= _maxMissedScans)
                    {
                        if (device.Status != "OFFLINE")
                        {
                            _logger.LogWarning(
                                "设备 {DeviceId} (HostId: {HostId}, IP: {IpAddress}) 连续 {MissedCount} 次扫描未发现，标记为离线",
                                device.Id, device.HostId, device.IpAddress, missedCount);
                            device.Status = "OFFLINE";
                        }
                    }
                    else
                    {
                        _logger.LogDebug(
                            "设备 {DeviceId} (HostId: {HostId}) 本次未发现，但在宽限期内 ({MissedCount}/{MaxMissed})，保持当前状态: {Status}",
                            device.Id, device.HostId, missedCount, _maxMissedScans, device.Status);
                    }

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

        /// <summary>
        /// 连续多少次扫描未发现设备后才标记为离线
        /// 这提供了一个"宽限期"，避免因网络波动导致的误判
        /// </summary>
        public int MaxMissedScansBeforeOffline { get; set; } = 3; // 默认连续3次未发现才离线

        /// <summary>
        /// 对未在广播扫描中发现的设备进行单独重试的次数
        /// </summary>
        public int RetryAttempts { get; set; } = 2; // 默认重试2次

        /// <summary>
        /// 重试之间的延迟时间（毫秒）
        /// </summary>
        public int RetryDelayMs { get; set; } = 500; // 默认500ms
    }
}

