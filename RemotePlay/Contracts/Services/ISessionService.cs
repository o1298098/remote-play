using RemotePlay.Models.PlayStation;

namespace RemotePlay.Contracts.Services
{
    public interface ISessionService
    {
        Task<RemoteSession> StartSessionAsync(
            string hostIp,
            DeviceCredentials credentials,
            string hostType,
            CancellationToken cancellationToken = default);

        Task<RemoteSession> StartSessionAsync(
            string hostIp,
            DeviceCredentials credentials,
            string hostType,
            SessionStartOptions options,
            CancellationToken cancellationToken = default);

        Task<bool> StopSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

        Task<bool> SendInputAsync(Guid sessionId, InputState input, CancellationToken cancellationToken = default);

        Task<RemoteSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<RemoteSession>> ListSessionsAsync(CancellationToken cancellationToken = default);

        Task<bool> WaitReadyAsync(Guid sessionId, TimeSpan timeout, CancellationToken cancellationToken = default);

        Task<bool> StandbyAsync(Guid sessionId, CancellationToken cancellationToken = default);
    }
}


