namespace RemotePlay.Models.Base
{
    public class ResponseModel
    {
        public bool Success { get; set; }
        public object? Result { get; set; }
        public int StatusCode { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
    }
}
