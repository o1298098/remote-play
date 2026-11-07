using Microsoft.AspNetCore.SignalR;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.WebRTC;
using RemotePlay.Services.WebRTC;
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

                _logger.LogDebug("🎯 SignalR 请求关键帧成功: SessionId={SessionId}, StreamingSessionId={StreamingSessionId}, ConnectionId={ConnectionId}",
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
        
        /// <summary>
        /// 浏览器定期回传的视频接收统计，仅用于写日志诊断
        /// </summary>
        public Task ReportClientStats(string sessionId, ClientVideoStats stats)
        {
            if (stats == null || !_logger.IsEnabled(LogLevel.Debug))
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug(
                "浏览器视频统计 [{Session}]: 状态={State} 收包={PacketsReceived} 丢包={PacketsLost} 收帧={FramesReceived} 解码={FramesDecoded} " +
                "关键帧解码={KeyFramesDecoded} 丢帧={FramesDropped} PLI={Pli} FIR={Fir} NACK={Nack} 冻结={FreezeCount}次/{FreezeSeconds:F1}s " +
                "解码帧率={DecodeFps:F1} 码率={Kbps:F0}kbps 抖动缓冲={JitterBufferMs:F0}ms 解码器={Decoder} 编码={Codec} 分辨率={Width}x{Height}",
                sessionId.Length > 8 ? sessionId[..8] : sessionId,
                stats.ConnectionState, stats.PacketsReceived, stats.PacketsLost, stats.FramesReceived, stats.FramesDecoded,
                stats.KeyFramesDecoded, stats.FramesDropped, stats.PliCount, stats.FirCount, stats.NackCount,
                stats.FreezeCount, stats.TotalFreezesDuration, stats.DecodeFps, stats.BitrateKbps, stats.JitterBufferDelayMs,
                stats.DecoderImplementation, stats.Codec, stats.FrameWidth, stats.FrameHeight);

            return Task.CompletedTask;
        }

        /// <summary>
        /// 处理 ICE Restart：当 ICE 连接断开时，重新协商
        /// </summary>
        public async Task HandleIceRestart(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                await Clients.Caller.SendAsync("IceRestartFailed", "SessionId 不能为空");
                return;
            }
            
            try
            {
                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    await Clients.Caller.SendAsync("IceRestartFailed", "WebRTC 会话不存在");
                    return;
                }
                
                // ✅ 触发 ICE Restart
                var success = await _signalingService.TryIceRestartAsync(sessionId);
                if (success)
                {
                    // ✅ 获取新的 Offer SDP
                    var newOffer = session.PeerConnection.localDescription?.sdp?.ToString();
                    if (!string.IsNullOrWhiteSpace(newOffer))
                    {
                        await Clients.Caller.SendAsync("IceRestartOffer", newOffer);
                        _logger.LogInformation("✅ ICE Restart Offer 已发送: SessionId={SessionId}", sessionId);
                    }
                    else
                    {
                        await Clients.Caller.SendAsync("IceRestartFailed", "无法获取新的 Offer");
                    }
                }
                else
                {
                    await Clients.Caller.SendAsync("IceRestartFailed", "ICE Restart 失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ ICE Restart 失败: SessionId={SessionId}", sessionId);
                await Clients.Caller.SendAsync("IceRestartFailed", "ICE Restart 异常");
            }
        }
        
        /// <summary>
        /// 获取待处理的 ICE Restart Offer
        /// </summary>
        public async Task<string?> GetIceRestartOffer(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }
            
            try
            {
                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    return null;
                }
                
                var offer = session.GetPendingIceRestartOffer();
                if (!string.IsNullOrWhiteSpace(offer))
                {
                    _logger.LogInformation("📤 返回 ICE Restart Offer: SessionId={SessionId}", sessionId);
                }
                return offer;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 获取 ICE Restart Offer 失败: SessionId={SessionId}", sessionId);
                return null;
            }
        }

        /// <summary>
        /// 强制重置 ReorderQueue（用户主动触发，解决画面冻结）
        /// </summary>
        public async Task ForceResetReorderQueue(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                await Clients.Caller.SendAsync("ReorderQueueResetResult", false);
                await Clients.Caller.SendAsync("Error", "SessionId 不能为空");
                return;
            }

            try
            {
                var session = _signalingService.GetSession(sessionId);
                if (session == null)
                {
                    await Clients.Caller.SendAsync("ReorderQueueResetResult", false);
                    await Clients.Caller.SendAsync("Error", "WebRTC 会话不存在");
                    return;
                }

                if (!session.StreamingSessionId.HasValue)
                {
                    await Clients.Caller.SendAsync("ReorderQueueResetResult", false);
                    await Clients.Caller.SendAsync("Error", "会话尚未绑定流，无法重置队列");
                    return;
                }

                var success = await _streamingService.ForceResetReorderQueueAsync(session.StreamingSessionId.Value);
                if (success)
                {
                    _logger.LogInformation("🔄 SignalR 强制重置 ReorderQueue 成功: SessionId={SessionId}, StreamingSessionId={StreamingSessionId}, ConnectionId={ConnectionId}",
                        sessionId, session.StreamingSessionId, Context.ConnectionId);

                    await Clients.Caller.SendAsync("ReorderQueueResetResult", true);
                }
                else
                {
                    await Clients.Caller.SendAsync("ReorderQueueResetResult", false);
                    await Clients.Caller.SendAsync("Error", "流不存在或已结束");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ SignalR 强制重置 ReorderQueue 失败: SessionId={SessionId}, ConnectionId={ConnectionId}", sessionId, Context.ConnectionId);
                await Clients.Caller.SendAsync("ReorderQueueResetResult", false);
                await Clients.Caller.SendAsync("Error", "重置队列失败");
            }
        }
    }
}

