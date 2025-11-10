namespace RemotePlay.Models.PlayStation
{
    public class ControllerStickRequest
    {
        public Guid SessionId { get; set; }
        public string StickName { get; set; } = string.Empty;  // left, right
        public string? Axis { get; set; }  // x, y
        public float? Value { get; set; }  // -1.0 to 1.0
        public StickPoint? Point { get; set; }
    }
}

