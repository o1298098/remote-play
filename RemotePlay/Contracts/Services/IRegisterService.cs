using RemotePlay.Models.PlayStation;
using RemotePlay.Utils.Crypto;

namespace RemotePlay.Contracts.Services
{
    public interface IRegisterService
    {
        Task<RegisterResult> RegisterDeviceAsync(string hostIp, string accountId, string pin, CancellationToken cancellationToken = default);
        (SessionCipher, byte[], byte[]) GetRegistCipherHeadersPayload(string hostType, string hostIp, string psnId, string pin);
    }
}
