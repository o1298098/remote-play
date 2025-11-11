using Microsoft.AspNetCore.SignalR;
using RemotePlay.Contracts.Services;
using RemotePlay.Services;
using RemotePlay.Services.Streaming;

namespace RemotePlay.Hubs
{
    /// <summary>
    /// WebRTC/流媒体相关的 SignalR Hub
    /// </summary>
    public class StreamingHub : Hub
    {
        private readonly WebRTCSignalingService _signalingService;
        private readonly IStreamingService _streamingService;
        private readonly ILogger<StreamingHub> _logger;

        public StreamingHub(
            WebRTCSignalingService signalingService,
            IStreamingService streamingService,
            ILogger<StreamingHub> logger)
        {
            _signalingService = signalingService;
            _streamingService = streamingService;
            _logger = logger;
        }

        /// <summary>
        /// 主动请求关键帧
        /// </summary>
        public async Task RequestKeyframe(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                await Clients.Caller.SendAsync("KeyframeRequested", false);
                await Clients.Caller.SendAsync("Error", "SessionId 不能为空");
                return;
            }

            try
            {
                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    await Clients.Caller.SendAsync("KeyframeRequested", false);
                    await Clients.Caller.SendAsync("Error", "WebRTC 会话不存在");
                    return;
                }

                if (!session.StreamingSessionId.HasValue)
                {
                    await Clients.Caller.SendAsync("KeyframeRequested", false);
                    await Clients.Caller.SendAsync("Error", "会话尚未绑定流，无法请求关键帧");
                    return;
                }

                var stream = await _streamingService.GetStreamAsync(session.StreamingSessionId.Value);
                if (stream == null)
                {
                    await Clients.Caller.SendAsync("KeyframeRequested", false);
                    await Clients.Caller.SendAsync("Error", "远程播放流不存在或已结束");
                    return;
                }

                await stream.RequestKeyframeAsync();

                _logger.LogInformation("🎯 SignalR 请求关键帧成功: SessionId={SessionId}, StreamingSessionId={StreamingSessionId}, ConnectionId={ConnectionId}",
                    sessionId, session.StreamingSessionId, Context.ConnectionId);

                await Clients.Caller.SendAsync("KeyframeRequested", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ SignalR 请求关键帧失败: SessionId={SessionId}, ConnectionId={ConnectionId}", sessionId, Context.ConnectionId);
                await Clients.Caller.SendAsync("KeyframeRequested", false);
                await Clients.Caller.SendAsync("Error", "请求关键帧失败");
            }
        }
    }
}

