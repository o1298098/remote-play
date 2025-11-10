using RemotePlay.Models.PlayStation;

namespace RemotePlay.Contracts.Services
{
    public interface IDeviceDiscoveryService
    {
        Task<List<ConsoleInfo>> DiscoverDevicesAsync(
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default);
        Task<ConsoleInfo?> DiscoverDeviceAsync(
            string hostIp,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default);
        Task<bool> WakeUpDeviceAsync(
           string host,
           string credential,
           string hostType,
           CancellationToken cancellationToken = default);


    }
}
