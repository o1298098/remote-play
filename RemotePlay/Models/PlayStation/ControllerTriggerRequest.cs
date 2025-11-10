using System;

namespace RemotePlay.Models.PlayStation
{
    public class ControllerTriggerRequest
    {
        public Guid SessionId { get; set; }
        public float? L2 { get; set; }
        public float? R2 { get; set; }
    }
}


