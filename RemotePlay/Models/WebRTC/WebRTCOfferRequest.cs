using System;

namespace RemotePlay.Models.WebRTC
{
    public class WebRTCOfferRequest
    {
        public Guid? RemotePlaySessionId { get; set; }
        public bool? PreferLanCandidates { get; set; }
    }
}

