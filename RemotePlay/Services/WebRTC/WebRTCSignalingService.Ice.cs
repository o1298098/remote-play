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
                _logger.LogWarning("⚠️ Session not found, cannot perform ICE restart: {SessionId}", sessionId);
                return false;
            }
            
            var sessionLock = _iceRestartLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            if (!await sessionLock.WaitAsync(0))
            {
                _logger.LogInformation("⏳ ICE restart already in progress, skipping duplicate request: {SessionId}", sessionId);
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
                        _logger.LogInformation("⏳ ICE restart backing off: {SessionId}, {Seconds}s remaining (attempts: {Attempts})", 
                            sessionId, backoffSeconds - (int)timeSinceLastAttempt, attempts);
                        return false;
                    }
                }
                
                _iceRestartAttempts.AddOrUpdate(sessionId, 1, (k, v) => v + 1);
                _iceRestartLastAttempt.AddOrUpdate(sessionId, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
                attempts = _iceRestartAttempts[sessionId];
                
                _logger.LogInformation("🔄 Starting ICE restart: {SessionId} (attempts: {Attempts})", sessionId, attempts);
                
                session.Receiver?.StopKeepalive();
                
                RTCSessionDescriptionInit? newOffer = null;
                try
                {
                    newOffer = session.PeerConnection.createOffer();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Exception creating ICE restart offer: {SessionId}", sessionId);
                    return false;
                }
                
                if (newOffer == null)
                {
                    _logger.LogWarning("⚠️ Creating ICE restart offer returned null: {SessionId}", sessionId);
                    return false;
                }
                
                try
                {
                    await session.PeerConnection.setLocalDescription(newOffer);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Failed to set ICE restart local description: {SessionId}", sessionId);
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
                        _logger.LogInformation("✅ Recreated keepalive DataChannel after ICE restart: {SessionId}", sessionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Failed to create DataChannel after ICE restart: {SessionId}", sessionId);
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
                        _logger.LogWarning("⚠️ ICE credentials missing after SDP optimization, using original SDP: {SessionId}", sessionId);
                        finalSdp = originalSdp;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ SDP optimization failed, using original SDP: {SessionId}", sessionId);
                    finalSdp = originalSdp;
                }
                
                session.AddPendingIceRestartOffer(finalSdp);
                _iceRestartPendingOfferTime.AddOrUpdate(sessionId, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
                
                _iceRestartAttempts.TryRemove(sessionId, out _);
                _iceRestartLastAttempt.TryRemove(sessionId, out _);
                
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation("✅ ICE restart offer created: {SessionId} in {ElapsedMs}ms, awaiting frontend renegotiation", 
                    sessionId, (int)elapsedMs);
                
                return true;
            }
            catch (Exception ex)
            {
                var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(ex, "❌ ICE restart failed: {SessionId} after {ElapsedMs}ms (attempts: {Attempts})", 
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
                        _logger.LogWarning("⏰ Cleaning up expired ICE restart offer: {SessionId} (unhandled for over 30s)", sessionId);
                        _iceRestartPendingOfferTime.TryRemove(sessionId, out _);
                    }
                }
            }
        }
    }
}

