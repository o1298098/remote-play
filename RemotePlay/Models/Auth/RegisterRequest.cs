using System.ComponentModel.DataAnnotations;

namespace RemotePlay.Models.Auth
{
    /// <summary>
    /// 用户注册请求模型
    /// </summary>
    public class RegisterRequest
    {
        [Required(ErrorMessage = "用户名是必填项")]
        [MinLength(3, ErrorMessage = "用户名至少需要3个字符")]
        [MaxLength(50, ErrorMessage = "用户名不能超过50个字符")]
        [RegularExpression(@"^[a-zA-Z0-9_]+$", ErrorMessage = "用户名只能包含字母、数字和下划线")]
        public required string Username { get; set; }

        [Required(ErrorMessage = "邮箱是必填项")]
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [MaxLength(100, ErrorMessage = "邮箱不能超过100个字符")]
        public required string Email { get; set; }

        [Required(ErrorMessage = "密码是必填项")]
        [MinLength(8, ErrorMessage = "密码至少需要8个字符")]
        [MaxLength(100, ErrorMessage = "密码不能超过100个字符")]
        public required string Password { get; set; }
    }
}

