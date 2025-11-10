using RemotePlay.Models.Auth;
using RemotePlay.Models.DB;

namespace RemotePlay.Contracts.Services
{
    /// <summary>
    /// 认证服务接口
    /// </summary>
    public interface IAuthService
    {
        /// <summary>
        /// 注册新用户
        /// </summary>
        Task<AuthResponse> RegisterAsync(RegisterRequest request);

        /// <summary>
        /// 用户登录
        /// </summary>
        Task<AuthResponse?> LoginAsync(LoginRequest request);

        /// <summary>
        /// 验证JWT令牌
        /// </summary>
        Task<User?> ValidateTokenAsync(string token);

        /// <summary>
        /// 根据用户名或邮箱查找用户
        /// </summary>
        Task<User?> FindUserByUsernameOrEmailAsync(string usernameOrEmail);

        /// <summary>
        /// 根据用户ID查找用户
        /// </summary>
        Task<User?> FindUserByIdAsync(string userId);
    }
}

