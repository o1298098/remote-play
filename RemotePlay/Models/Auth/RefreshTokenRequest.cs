using System.ComponentModel.DataAnnotations;

namespace RemotePlay.Models.Auth
{
    /// <summary>
    /// 刷新访问令牌请求
    /// </summary>
    public class RefreshTokenRequest
    {
        [Required]
        public required string RefreshToken { get; set; }
    }
}
