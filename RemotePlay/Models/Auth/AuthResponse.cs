namespace RemotePlay.Models.Auth
{
    /// <summary>
    /// 认证响应模型
    /// </summary>
    public class AuthResponse
    {
        public required string Token { get; set; }
        public required string Username { get; set; }
        public required string Email { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}

