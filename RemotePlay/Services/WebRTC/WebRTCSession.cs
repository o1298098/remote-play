using RemotePlay.Services.Streaming.Receiver;
using SIPSorcery.Net;

namespace RemotePlay.Services.WebRTC
{
    /// <summary>
    /// WebRTC 会话信息
    /// </summary>
    public class WebRTCSession
    {
        public required string SessionId { get; init; }
        public required RTCPeerConnection PeerConnection { get; init; }
        public required WebRTCReceiver Receiver { get; init; }
        public DateTime CreatedAt { get; init; }
        public Guid? StreamingSessionId { get; set; }
        public string? PreferredVideoCodec { get; init; }
        
        private readonly List<RTCIceCandidateInit> _pendingIceCandidates = new();
        private readonly HashSet<string> _candidateKeys = new();
        private readonly object _candidatesLock = new();
        
        private string? _pendingIceRestartOffer = null;
        private DateTime _pendingIceRestartOfferTime = DateTime.MinValue;
        private readonly object _iceRestartLock = new();

        public RTCPeerConnectionState ConnectionState => PeerConnection.connectionState;
        public RTCIceConnectionState IceConnectionState => PeerConnection.iceConnectionState;
        
        public List<RTCIceCandidateInit> GetPendingIceCandidates()
        {
            lock (_candidatesLock)
            {
                var result = _pendingIceCandidates.ToList();
                _pendingIceCandidates.Clear();
                _candidateKeys.Clear();
                return result;
            }
        }
        
        public void ClearPendingIceCandidates()
        {
            lock (_candidatesLock)
            {
                _pendingIceCandidates.Clear();
                _candidateKeys.Clear();
            }
        }
        
        public void AddPendingIceCandidate(RTCIceCandidateInit candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidate))
            {
                return;
            }

            lock (_candidatesLock)
            {
                var candidateKey = GetCandidateCoreKey(candidate.candidate);
                
                if (!_candidateKeys.Contains(candidateKey))
                {
                    _candidateKeys.Add(candidateKey);
                    _pendingIceCandidates.Add(candidate);
                }
                else
                {
                    var existingIndex = _pendingIceCandidates.FindIndex(c => GetCandidateCoreKey(c.candidate) == candidateKey);
                    if (existingIndex >= 0)
                    {
                        var existing = _pendingIceCandidates[existingIndex];
                        var existingHasUfrag = existing.candidate?.ToLowerInvariant().Contains("ufrag") ?? false;
                        var newHasUfrag = candidate.candidate?.ToLowerInvariant().Contains("ufrag") ?? false;
                        
                        if ((!existingHasUfrag && newHasUfrag) || 
                            (newHasUfrag && existingHasUfrag && candidate.candidate != existing.candidate))
                        {
                            _pendingIceCandidates[existingIndex] = candidate;
                        }
                    }
                }
            }
        }
        
        private string GetCandidateCoreKey(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
            
            var parts = candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var coreParts = new List<string>();
            
            foreach (var part in parts)
            {
                var partLower = part.ToLowerInvariant();
                if (partLower == "ufrag" || partLower == "generation" || partLower == "network-cost" ||
                    (coreParts.Count > 0 && (coreParts[coreParts.Count - 1].ToLowerInvariant() == "ufrag" ||
                                             coreParts[coreParts.Count - 1].ToLowerInvariant() == "generation" ||
                                             coreParts[coreParts.Count - 1].ToLowerInvariant() == "network-cost")))
                {
                    continue;
                }
                coreParts.Add(part);
            }
            
            return string.Join(" ", coreParts);
        }
        
        public void AddPendingIceRestartOffer(string offerSdp)
        {
            lock (_iceRestartLock)
            {
                _pendingIceRestartOffer = offerSdp;
                _pendingIceRestartOfferTime = DateTime.UtcNow;
            }
        }
        
        public string? GetPendingIceRestartOffer()
        {
            lock (_iceRestartLock)
            {
                var offer = _pendingIceRestartOffer;
                _pendingIceRestartOffer = null;
                _pendingIceRestartOfferTime = DateTime.MinValue;
                return offer;
            }
        }
        
        public bool HasPendingIceRestartOffer()
        {
            lock (_iceRestartLock)
            {
                if (string.IsNullOrWhiteSpace(_pendingIceRestartOffer))
                    return false;
                
                if ((DateTime.UtcNow - _pendingIceRestartOfferTime).TotalSeconds > 30)
                {
                    _pendingIceRestartOffer = null;
                    _pendingIceRestartOfferTime = DateTime.MinValue;
                    return false;
                }
                
                return true;
            }
        }
    }
}

