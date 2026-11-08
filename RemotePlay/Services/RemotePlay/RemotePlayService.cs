using Microsoft.Extensions.Options;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Configuration;
using RemotePlay.Models.PlayStation;

namespace RemotePlay.Services.RemotePlay
{
    public interface IRemotePlayService
    {
        Task<List<ConsoleInfo>> DiscoverDevicesAsync(int? timeoutMs = null, CancellationToken cancellationToken = default);
        Task<ConsoleInfo?> DiscoverDeviceAsync(string hostIp, int? timeoutMs = null, CancellationToken cancellationToken = default);
        Task<bool> WakeUpDeviceAsync(
          string host,
          string credential,
          string hostType,
          CancellationToken cancellationToken = default);
        Task<RegisterResult> RegisterDeviceAsync(string hostIp, string accountId, string pin, CancellationToken cancellationToken = default);
        Task<RegisterResult> RegisterDeviceAsync(ConsoleInfo device, string accountId, string pin, CancellationToken cancellationToken = default);
        Task<bool> ValidateCredentialsAsync(DeviceCredentials credentials, CancellationToken cancellationToken = default);
    }

    public class RemotePlayService : IRemotePlayService
    {
        private readonly ILogger<RemotePlayService> _logger;
        private readonly IDeviceDiscoveryService _discoveryService;
        private readonly IRegisterService _registerService;
        private readonly RemotePlayConfig _config;

        public RemotePlayService(
            ILogger<RemotePlayService> logger,
            IDeviceDiscoveryService discoveryService,
            IRegisterService registerService,
            IOptions<RemotePlayConfig> config)
        {
            _logger = logger;
            _discoveryService = discoveryService;
            _registerService = registerService;
            _config = config.Value;
        }

        public async Task<List<ConsoleInfo>> DiscoverDevicesAsync(int? timeoutMs = null, CancellationToken cancellationToken = default)
        {
            var timeout = timeoutMs ?? _config.Discovery.TimeoutMs;
            _logger.LogInformation("Starting device discovery, timeout: {TimeoutMs}ms", timeout);

            try
            {
                var devices = await _discoveryService.DiscoverDevicesAsync(timeout, cancellationToken);
                _logger.LogInformation("Device discovery complete, found {Count} device(s)", devices.Count);
                return devices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device discovery");
                throw;
            }
        }

        public async Task<ConsoleInfo?> DiscoverDeviceAsync(string hostIp, int? timeoutMs = null, CancellationToken cancellationToken = default)
        {
            var timeout = timeoutMs ?? _config.Discovery.TimeoutMs;
            _logger.LogInformation("Discovering specific device: {HostIp}", hostIp);

            try
            {
                var device = await _discoveryService.DiscoverDeviceAsync(hostIp, timeout, cancellationToken);
                if (device != null)
                {
                    _logger.LogInformation("Discovered device: {DeviceName} ({DeviceIp})", device.Name, device.Ip);
                }
                else
                {
                    _logger.LogWarning("Device not found: {HostIp}", hostIp);
                }
                return device;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering device: {HostIp}", hostIp);
                throw;
            }
        }


        public async Task<bool> WakeUpDeviceAsync(
          string host,
          string credential,
          string hostType,
          CancellationToken cancellationToken = default)
        {
            return await _discoveryService.WakeUpDeviceAsync(host, credential, hostType, cancellationToken);
        }
        public async Task<RegisterResult> RegisterDeviceAsync(string hostIp, string accountId, string pin, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting device registration - host: {HostIp}, account: {AccountId}", hostIp, accountId);

            try
            {
                // 验证输入参数
                ValidateRegistrationParameters(hostIp, accountId, pin);

                var result = await _registerService.RegisterDeviceAsync(hostIp, accountId, pin, cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation("Device registered successfully - host: {HostName} ({HostId})", result.HostName, result.HostId);
                }
                else
                {
                    _logger.LogWarning("Device registration failed - host: {HostIp}, error: {ErrorMessage}", hostIp, result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device registration - host: {HostIp}", hostIp);
                return new RegisterResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<RegisterResult> RegisterDeviceAsync(ConsoleInfo device, string accountId, string pin, CancellationToken cancellationToken = default)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            return await RegisterDeviceAsync(device.Ip, accountId, pin, cancellationToken);
        }

        public async Task<bool> ValidateCredentialsAsync(DeviceCredentials credentials, CancellationToken cancellationToken = default)
        {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));

            _logger.LogDebug("Validating device credentials - host: {HostName} ({HostId})", credentials.HostName, credentials.HostId);

            try
            {
                // 检查凭据是否过期
                if (!credentials.IsValid)
                {
                    _logger.LogWarning("Device credentials expired - host: {HostName}", credentials.HostName);
                    return false;
                }

                // 检查必要字段
                if (string.IsNullOrEmpty(credentials.HostId) ||
                    string.IsNullOrEmpty(credentials.HostIp) ||
                    credentials.RegistrationKey.Length == 0 ||
                    credentials.ServerKey.Length == 0)
                {
                    _logger.LogWarning("Device credentials incomplete - host: {HostName}", credentials.HostName);
                    return false;
                }

                // 尝试ping主机以验证连接性
                var device = await _discoveryService.DiscoverDeviceAsync(credentials.HostIp, 5000, cancellationToken);
                if (device == null)
                {
                    _logger.LogWarning("Cannot reach host - {HostIp}", credentials.HostIp);
                    return false;
                }

                _logger.LogDebug("Device credentials validated - host: {HostName}", credentials.HostName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating device credentials - host: {HostName}", credentials.HostName);
                return false;
            }
        }

        private static void ValidateRegistrationParameters(string hostIp, string accountId, string pin)
        {
            if (string.IsNullOrWhiteSpace(hostIp))
                throw new ArgumentException("主机IP不能为空", nameof(hostIp));
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException("账户ID不能为空", nameof(accountId));
            if (string.IsNullOrWhiteSpace(pin))
                throw new ArgumentException("PIN不能为空", nameof(pin));

            // 验证IP地址格式
            if (!System.Net.IPAddress.TryParse(hostIp, out _))
                throw new ArgumentException("无效的IP地址格式", nameof(hostIp));

            // 验证PIN格式（应该是数字）
            if (!pin.All(char.IsDigit))
                throw new ArgumentException("PIN必须为数字", nameof(pin));
        }
    }
}
