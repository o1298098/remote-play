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
            _logger.LogInformation("开始设备发现，超时时间: {TimeoutMs}ms", timeout);

            try
            {
                var devices = await _discoveryService.DiscoverDevicesAsync(timeout, cancellationToken);
                _logger.LogInformation("设备发现完成，找到 {Count} 个设备", devices.Count);
                return devices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备发现过程中发生错误");
                throw;
            }
        }

        public async Task<ConsoleInfo?> DiscoverDeviceAsync(string hostIp, int? timeoutMs = null, CancellationToken cancellationToken = default)
        {
            var timeout = timeoutMs ?? _config.Discovery.TimeoutMs;
            _logger.LogInformation("尝试发现特定设备: {HostIp}", hostIp);

            try
            {
                var device = await _discoveryService.DiscoverDeviceAsync(hostIp, timeout, cancellationToken);
                if (device != null)
                {
                    _logger.LogInformation("成功发现设备: {DeviceName} ({DeviceIp})", device.Name, device.Ip);
                }
                else
                {
                    _logger.LogWarning("未发现设备: {HostIp}", hostIp);
                }
                return device;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发现设备时发生错误: {HostIp}", hostIp);
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
            _logger.LogInformation("开始设备注册 - 主机: {HostIp}, 账户: {AccountId}", hostIp, accountId);

            try
            {
                // 验证输入参数
                ValidateRegistrationParameters(hostIp, accountId, pin);

                var result = await _registerService.RegisterDeviceAsync(hostIp, accountId, pin, cancellationToken);

                if (result.Success)
                {
                    _logger.LogInformation("设备注册成功 - 主机: {HostName} ({HostId})", result.HostName, result.HostId);
                }
                else
                {
                    _logger.LogWarning("设备注册失败 - 主机: {HostIp}, 错误: {ErrorMessage}", hostIp, result.ErrorMessage);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备注册时发生错误 - 主机: {HostIp}", hostIp);
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

            _logger.LogDebug("验证设备凭据 - 主机: {HostName} ({HostId})", credentials.HostName, credentials.HostId);

            try
            {
                // 检查凭据是否过期
                if (!credentials.IsValid)
                {
                    _logger.LogWarning("设备凭据已过期 - 主机: {HostName}", credentials.HostName);
                    return false;
                }

                // 检查必要字段
                if (string.IsNullOrEmpty(credentials.HostId) ||
                    string.IsNullOrEmpty(credentials.HostIp) ||
                    credentials.RegistrationKey.Length == 0 ||
                    credentials.ServerKey.Length == 0)
                {
                    _logger.LogWarning("设备凭据不完整 - 主机: {HostName}", credentials.HostName);
                    return false;
                }

                // 尝试ping主机以验证连接性
                var device = await _discoveryService.DiscoverDeviceAsync(credentials.HostIp, 5000, cancellationToken);
                if (device == null)
                {
                    _logger.LogWarning("无法连接到主机 - {HostIp}", credentials.HostIp);
                    return false;
                }

                _logger.LogDebug("设备凭据验证成功 - 主机: {HostName}", credentials.HostName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证设备凭据时发生错误 - 主机: {HostName}", credentials.HostName);
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
