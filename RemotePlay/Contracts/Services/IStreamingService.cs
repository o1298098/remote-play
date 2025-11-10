using RemotePlay.Models.PlayStation;
using RemotePlay.Services.Streaming;

namespace RemotePlay.Contracts.Services
{
    public interface IStreamingService
    {
        Task<bool> StartStreamAsync(Guid sessionId, bool isTest = true, CancellationToken cancellationToken = default);
        Task<bool> StopStreamAsync(Guid sessionId, CancellationToken cancellationToken = default);
        Task<bool> AttachReceiverAsync(Guid sessionId, IAVReceiver receiver, CancellationToken cancellationToken = default);
        Task<RPStreamV2?> GetStreamAsync(Guid sessionId);
        Task<RemoteSession?> GetSessionAsync(Guid sessionId);
        Task<bool> IsStreamRunningAsync(Guid sessionId);
    }
}


