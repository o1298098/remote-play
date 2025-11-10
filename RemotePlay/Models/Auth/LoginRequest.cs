using System.ComponentModel.DataAnnotations;

namespace RemotePlay.Models.Auth
{
    /// <summary>
    /// 用户登录请求模型
    /// </summary>
    public class LoginRequest
    {
        [Required(ErrorMessage = "用户名或邮箱是必填项")]
        public required string UsernameOrEmail { get; set; }

        [Required(ErrorMessage = "密码是必填项")]
        public required string Password { get; set; }
    }
}

