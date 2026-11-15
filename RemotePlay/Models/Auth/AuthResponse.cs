namespace RemotePlay.Models.Auth
{
    /// <summary>
    /// 认证响应模型
    /// </summary>
    public class AuthResponse
    {
        public required string Token { get; set; }
        /// <summary>
        /// 用于在访问令牌过期后自动续期（默认 30 天）
        /// </summary>
        public required string RefreshToken { get; set; }
        public required string Username { get; set; }
        public required string Email { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime RefreshExpiresAt { get; set; }
    }
}

