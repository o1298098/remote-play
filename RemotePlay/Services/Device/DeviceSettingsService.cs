using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Context;
using RemotePlay.Models.DB.PlayStation;
using RemotePlay.Models.PlayStation;

namespace RemotePlay.Services.Device
{
    public class DeviceSettingsService : IDeviceSettingsService
    {
        private static readonly string[] StreamingSettingKeys =
        {
            "stream.resolution",
            "stream.frame_rate",
            "stream.bitrate",
            "stream.quality",
            "stream.type"
        };

        private const string StreamingConfigType = "Streaming";

        private static readonly string[] StreamingEnumTypes =
        {
            "ResolutionPreset",
            "FPS",
            "Quality",
            "StreamType"
        };

        private readonly RPContext _context;
        private readonly ILogger<DeviceSettingsService> _logger;
        private readonly SessionConfig _sessionConfig;

        public DeviceSettingsService(
            RPContext context,
            ILogger<DeviceSettingsService> logger,
            IOptions<SessionConfig> sessionOptions)
        {
            _context = context;
            _logger = logger;
            _sessionConfig = sessionOptions.Value;
        }

        public async Task<IReadOnlyList<UserDeviceDto>> GetUserDevicesAsync(string userId, CancellationToken cancellationToken)
        {
            var userDevices = await _context.UserDevices
                .Where(ud => ud.UserId == userId && ud.IsActive)
                .Include(ud => ud.Device)
                .ToListAsync(cancellationToken);

            if (userDevices.Count == 0)
            {
                return Array.Empty<UserDeviceDto>();
            }

            var deviceIds = userDevices.Select(ud => ud.DeviceId).Distinct().ToList();

            var configs = await _context.DeviceConfigs
                .AsNoTracking()
                .Where(dc => deviceIds.Contains(dc.DeviceId) && dc.UserId == userId && StreamingSettingKeys.Contains(dc.ConfigKey) && dc.IsActive)
                .ToListAsync(cancellationToken);

            var configLookup = configs
                .GroupBy(c => c.DeviceId)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(
                        c => c.ConfigKey,
                        c => NormalizeConfigValue(c.ConfigValue)));

            var result = new List<UserDeviceDto>(userDevices.Count);

            foreach (var ud in userDevices)
            {
                if (ud.Device == null)
                {
                    continue;
                }

                configLookup.TryGetValue(ud.DeviceId, out var settingsDictionary);
                var settings = BuildDeviceSettingsFromDictionary(settingsDictionary);
                ApplyDefaultValues(settings, null);

                result.Add(new UserDeviceDto
                {
                    UserDeviceId = ud.Id,
                    DeviceId = ud.Device.Id,
                    HostId = ud.Device.HostId,
                    HostName = ud.DeviceName ?? ud.Device.HostName,
                    HostType = ud.DeviceType ?? ud.Device.HostType,
                    IpAddress = ud.Device.IpAddress,
                    SystemVersion = ud.Device.SystemVersion,
                    IsRegistered = ud.Device.IsRegistered ?? false,
                    Status = ud.Device.Status,
                    LastUsedAt = ud.LastUsedAt,
                    CreatedAt = ud.CreatedAt,
                    Settings = settings
                });
            }

            return result;
        }

        public async Task<DeviceSettingsResponse> GetDeviceSettingsAsync(string userId, string deviceId, CancellationToken cancellationToken)
        {
            await EnsureUserDeviceAsync(userId, deviceId, cancellationToken);
            return await BuildDeviceSettingsResponseAsync(userId, deviceId, cancellationToken);
        }

        public async Task<DeviceSettingsResponse> UpdateDeviceSettingsAsync(string userId, string deviceId, UpdateDeviceSettingsRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            await EnsureUserDeviceAsync(userId, deviceId, cancellationToken);

            var timestamp = DateTime.UtcNow;

            await UpsertDeviceConfigAsync(userId, deviceId, "stream.resolution", request.Resolution, timestamp, cancellationToken);
            await UpsertDeviceConfigAsync(userId, deviceId, "stream.frame_rate", request.FrameRate, timestamp, cancellationToken);
            await UpsertDeviceConfigAsync(userId, deviceId, "stream.bitrate", request.Bitrate, timestamp, cancellationToken);
            await UpsertDeviceConfigAsync(userId, deviceId, "stream.quality", request.Quality, timestamp, cancellationToken);
            await UpsertDeviceConfigAsync(userId, deviceId, "stream.type", request.StreamType, timestamp, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);

            return await BuildDeviceSettingsResponseAsync(userId, deviceId, cancellationToken);
        }

        public async Task<DeviceStreamingSettings> GetEffectiveSettingsAsync(string userId, string deviceId, CancellationToken cancellationToken)
        {
            await EnsureUserDeviceAsync(userId, deviceId, cancellationToken);

            var configs = await _context.DeviceConfigs
                .AsNoTracking()
                .Where(dc => dc.DeviceId == deviceId && dc.UserId == userId && dc.IsActive && StreamingSettingKeys.Contains(dc.ConfigKey))
                .ToListAsync(cancellationToken);

            var values = configs.ToDictionary(
                c => c.ConfigKey,
                c => NormalizeConfigValue(c.ConfigValue));

            var settings = BuildDeviceSettingsFromDictionary(values);
            ApplyDefaultValues(settings, null);
            return settings;
        }

        private async Task<DeviceSettingsResponse> BuildDeviceSettingsResponseAsync(string userId, string deviceId, CancellationToken cancellationToken)
        {
            var configs = await _context.DeviceConfigs
                .AsNoTracking()
                .Where(dc => dc.DeviceId == deviceId && dc.UserId == userId && dc.IsActive && StreamingSettingKeys.Contains(dc.ConfigKey))
                .ToListAsync(cancellationToken);

            var values = configs.ToDictionary(
                c => c.ConfigKey,
                c => NormalizeConfigValue(c.ConfigValue));

            var settings = BuildDeviceSettingsFromDictionary(values);
            var options = await LoadDeviceSettingOptionsAsync(cancellationToken);

            ApplyDefaultValues(settings, options);

            return new DeviceSettingsResponse
            {
                Settings = settings,
                Options = options
            };
        }

        private async Task EnsureUserDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken)
        {
            var exists = await _context.UserDevices
                .AsNoTracking()
                .AnyAsync(ud => ud.UserId == userId && ud.DeviceId == deviceId && ud.IsActive, cancellationToken);

            if (!exists)
            {
                throw new InvalidOperationException("未找到设备或没有访问权限");
            }
        }

        private DeviceStreamingSettings BuildDeviceSettingsFromDictionary(IReadOnlyDictionary<string, string?>? values)
        {
            var settings = new DeviceStreamingSettings
            {
                Resolution = GetConfigValue(values, "stream.resolution"),
                FrameRate = GetConfigValue(values, "stream.frame_rate"),
                Bitrate = GetConfigValue(values, "stream.bitrate"),
                Quality = NormalizeQuality(GetConfigValue(values, "stream.quality")),
                StreamType = GetConfigValue(values, "stream.type")
            };

            return settings;
        }

        private async Task<DeviceStreamingOptions> LoadDeviceSettingOptionsAsync(CancellationToken cancellationToken)
        {
            var enumItems = await _context.Enums
                .AsNoTracking()
                .Where(e => e.IsActive && StreamingEnumTypes.Contains(e.EnumType))
                .OrderBy(e => e.EnumType)
                .ThenBy(e => e.SortOrder)
                .ToListAsync(cancellationToken);

            var options = new DeviceStreamingOptions();

            foreach (var item in enumItems)
            {
                switch (item.EnumType)
                {
                    case "ResolutionPreset":
                        if (!string.IsNullOrWhiteSpace(item.EnumValue))
                        {
                            try
                            {
                                var json = JObject.Parse(item.EnumValue);
                                options.Resolutions.Add(new DeviceResolutionOption
                                {
                                    Key = item.EnumKey,
                                    Label = item.Description ?? item.EnumKey,
                                    LabelKey = item.EnumCode ?? item.EnumKey,
                                    Width = json.Value<int?>("width") ?? 0,
                                    Height = json.Value<int?>("height") ?? 0,
                                    Bitrate = json.Value<int?>("bitrate") ?? 0
                                });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "解析分辨率枚举失败: {EnumKey}", item.EnumKey);
                            }
                        }
                        break;
                    case "FPS":
                        options.FrameRates.Add(new DeviceFrameRateOption
                        {
                            Value = string.IsNullOrWhiteSpace(item.EnumValue) ? item.EnumKey : item.EnumValue,
                            Label = item.Description ?? item.EnumKey,
                            LabelKey = item.EnumCode ?? item.EnumKey,
                            Fps = string.IsNullOrWhiteSpace(item.EnumValue) ? item.EnumKey : item.EnumValue
                        });
                        break;
                    case "Quality":
                        options.Bitrates.Add(new DeviceBitrateOption
                        {
                            Bitrate = string.IsNullOrWhiteSpace(item.EnumValue) ? "0" : item.EnumValue,
                            Label = item.Description ?? item.EnumKey,
                            Quality = item.EnumKey.ToLowerInvariant(),
                            LabelKey = item.EnumCode ?? item.EnumKey
                        });
                        break;
                    case "StreamType":
                        options.StreamTypes.Add(new DeviceStreamTypeOption
                        {
                            Value = string.IsNullOrWhiteSpace(item.EnumValue) ? "1" : item.EnumValue,
                            Label = item.Description ?? item.EnumKey,
                            Code = item.EnumKey,
                            LabelKey = item.EnumCode ?? item.EnumKey
                        });
                        break;
                }
            }

            return options;
        }

        private void ApplyDefaultValues(DeviceStreamingSettings settings, DeviceStreamingOptions? options)
        {
            settings.Resolution ??= _sessionConfig.DefaultResolution;
            settings.FrameRate ??= _sessionConfig.DefaultFps;
            settings.Quality ??= _sessionConfig.DefaultQuality;

            if (options != null)
            {
                if (string.IsNullOrWhiteSpace(settings.Bitrate) && !string.IsNullOrWhiteSpace(settings.Quality))
                {
                    var matched = options.Bitrates.FirstOrDefault(b =>
                        string.Equals(b.Quality, settings.Quality, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        settings.Bitrate = matched.Bitrate;
                    }
                }

                settings.Bitrate ??= options.Bitrates.FirstOrDefault()?.Bitrate;

                if (string.IsNullOrWhiteSpace(settings.StreamType))
                {
                    settings.StreamType = options.StreamTypes.FirstOrDefault()?.Value;
                }
            }

            settings.Bitrate ??= "0";
        }

        private async Task UpsertDeviceConfigAsync(string userId, string deviceId, string key, string? value, DateTime timestampUtc, CancellationToken cancellationToken)
        {
            string? normalizedValue = key switch
            {
                "stream.quality" => NormalizeQuality(value),
                _ => NormalizeConfigValue(value)
            };

            var existing = await _context.DeviceConfigs
                .FirstOrDefaultAsync(dc => dc.DeviceId == deviceId && dc.UserId == userId && dc.ConfigKey == key, cancellationToken);

            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                if (existing != null)
                {
                    _context.DeviceConfigs.Remove(existing);
                }
                return;
            }

            if (existing == null)
            {
                var config = new DeviceConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    DeviceId = deviceId,
                    UserId = userId,
                    ConfigKey = key,
                    ConfigValue = normalizedValue,
                    ConfigType = StreamingConfigType,
                    IsActive = true,
                    CreatedAt = timestampUtc,
                    UpdatedAt = timestampUtc
                };
                _context.DeviceConfigs.Add(config);
            }
            else
            {
                existing.ConfigValue = normalizedValue;
                existing.ConfigType = string.IsNullOrWhiteSpace(existing.ConfigType) ? StreamingConfigType : existing.ConfigType;
                existing.IsActive = true;
                existing.UpdatedAt = timestampUtc;
                if (string.IsNullOrWhiteSpace(existing.UserId))
                {
                    existing.UserId = userId;
                }
            }
        }

        private static string? NormalizeConfigValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            return value.Trim();
        }

        private static string? NormalizeQuality(string? value)
        {
            var normalized = NormalizeConfigValue(value);
            return normalized?.ToLowerInvariant();
        }

        private static string? GetConfigValue(IReadOnlyDictionary<string, string?>? values, string key)
        {
            if (values != null && values.TryGetValue(key, out var result))
            {
                return NormalizeConfigValue(result);
            }
            return null;
        }
    }
}


