using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemotePlay.Models.PlayStation;

namespace RemotePlay.Contracts.Services
{
    public interface IDeviceSettingsService
    {
        Task<IReadOnlyList<UserDeviceDto>> GetUserDevicesAsync(string userId, CancellationToken cancellationToken);

        Task<DeviceSettingsResponse> GetDeviceSettingsAsync(string userId, string deviceId, CancellationToken cancellationToken);

        Task<DeviceSettingsResponse> UpdateDeviceSettingsAsync(string userId, string deviceId, UpdateDeviceSettingsRequest request, CancellationToken cancellationToken);

        Task<DeviceStreamingSettings> GetEffectiveSettingsAsync(string userId, string deviceId, CancellationToken cancellationToken);
    }
}


