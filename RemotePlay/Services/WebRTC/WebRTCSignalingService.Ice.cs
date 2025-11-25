using System.Collections.Concurrent;
using SIPSorcery.Net;

namespace RemotePlay.Services.WebRTC
{
    /// <summary>
    /// WebRTCSignalingService ICE 管理部分
    /// </summary>
    public partial class WebRTCSignalingService
    {
        private readonly ConcurrentDictionary<string, int> _iceRestartAttempts = new();
        private readonly ConcurrentDictionary<string, DateTime> _iceRestartLastAttempt = new();
        private readonly ConcurrentDictionary<string, DateTime> _iceRestartPendingOfferTime = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _iceRestartLocks = new();
        
        public async Task<bool> TryIceRestartAsync(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                _logger.LogWarning("⚠️ 会话不存在，无法执行 ICE Restart: {SessionId}", sessionId);
                return false;
            }
            
            var sessionLock = _iceRestartLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            if (!await sessionLock.WaitAsync(0))
            {
                _logger.LogInformation("⏳ ICE Restart 正在进行中，跳过重复请求: {SessionId}", sessionId);
                return false;
            }
            
            var startTime = DateTime.UtcNow;
            
            try
            {
                var attempts = _iceRestartAttempts.GetOrAdd(sessionId, 0);
                if (_iceRestartLastAttempt.TryGetValue(sessionId, out var lastAttempt))
                {
                    var timeSinceLastAttempt = (DateTime.UtcNow - lastAttempt).TotalSeconds;
                    int backoffSeconds = attempts switch
                    {
                        0 => 5,
                        1 => 20,
                        _ => 60
                    };
                    
                    if (timeSinceLastAttempt < backoffSeconds)
                    {
                        _logger.LogInformation("⏳ ICE Restart 退避中: {SessionId}，还需等待 {Seconds} 秒 (尝试次数: {Attempts})", 
                            sessionId, backoffSeconds - (int)timeSinceLastAttempt, attempts);
                        return false;
                    }
                }
                
                _iceRestartAttempts.AddOrUpdate(sessionId, 1, (k, v) => v + 1);
                _iceRestartLastAttempt.AddOrUpdate(sessionId, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
                attempts = _iceRestartAttempts[sessionId];
                
                _logger.LogInformation("🔄 开始 ICE Restart: {SessionId} (尝试次数: {Attempts})", sessionId, attempts);
                
                session.Receiver?.StopKeepalive();
                
                RTCSessionDescriptionInit? newOffer = null;
                try
                {
                    newOffer = session.PeerConnection.createOffer();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 创建 ICE Restart Offer 异常: {SessionId}", sessionId);
                    return false;
                }
                
                if (newOffer == null)
                {
                    _logger.LogWarning("⚠️ 创建 ICE Restart Offer 返回 null: {SessionId}", sessionId);
                    return false;
                }
                
                try
                {
                    await session.PeerConnection.setLocalDescription(newOffer);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 设置 ICE Restart LocalDescription 失败: {SessionId}", sessionId);
                    return false;
                }
                
                RTCDataChannel? newKeepaliveChannel = null;
                try
                {
                    var dataChannelInit = new RTCDataChannelInit
                    {
                        ordered = true,
                        maxRetransmits = 0,
                        maxPacketLifeTime = null
                    };
                    
                    newKeepaliveChannel = await session.PeerConnection.createDataChannel("keepalive", dataChannelInit);
                    
                    if (newKeepaliveChannel != null)
                    {
                        session.Receiver?.SetKeepaliveDataChannel(newKeepaliveChannel);
                        _logger.LogInformation("✅ ICE Restart 后重新创建 Keepalive DataChannel: {SessionId}", sessionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ ICE Restart 后创建 DataChannel 失败: {SessionId}", sessionId);
                }
                
                var originalSdp = newOffer.sdp.ToString();
                var finalSdp = originalSdp;
                try
                {
                    finalSdp = OptimizeSdpForLowLatency(finalSdp);
                    finalSdp = ApplyPublicIpToSdp(finalSdp);
                    finalSdp = PrioritizeLanCandidates(finalSdp, null);
                    
                    if (!finalSdp.Contains("ice-ufrag") || !finalSdp.Contains("ice-pwd"))
                    {
                        _logger.LogWarning("⚠️ SDP 优化后缺少 ICE credentials，使用原始 SDP: {SessionId}", sessionId);
                        finalSdp = originalSdp;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ SDP 优化失败，使用原始 SDP: {SessionId}", sessionId);
                    finalSdp = originalSdp;
                }
                
                session.AddPendingIceRestartOffer(finalSdp);
                _iceRestartPendingOfferTime.AddOrUpdate(sessionId, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
                
                _iceRestartAttempts.TryRemove(sessionId, out _);
                _iceRestartLastAttempt.TryRemove(sessionId, out _);
                
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation("✅ ICE Restart Offer 已创建: {SessionId}，耗时 {ElapsedMs}ms，等待前端重新协商", 
                    sessionId, (int)elapsedMs);
                
                return true;
            }
            catch (Exception ex)
            {
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(ex, "❌ ICE Restart 失败: {SessionId}，耗时 {ElapsedMs}ms (尝试次数: {Attempts})", 
                    sessionId, (int)elapsedMs, _iceRestartAttempts.GetOrAdd(sessionId, 0));
                return false;
            }
            finally
            {
                sessionLock.Release();
            }
        }
        
        public void CleanupExpiredIceRestartOffers()
        {
            var expired = _iceRestartPendingOfferTime.Where(kvp =>
                (DateTime.UtcNow - kvp.Value).TotalSeconds > 30
            ).ToList();
            
            foreach (var (sessionId, _) in expired)
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    var offer = session.GetPendingIceRestartOffer();
                    if (!string.IsNullOrWhiteSpace(offer))
                    {
                        _logger.LogWarning("⏰ 清理过期的 ICE Restart Offer: {SessionId} (超过 30 秒未处理)", sessionId);
                        _iceRestartPendingOfferTime.TryRemove(sessionId, out _);
                    }
                }
            }
        }
    }
}

