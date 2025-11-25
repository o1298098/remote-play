using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RemotePlay.Contracts.Services;
using RemotePlay.Models.Auth;
using RemotePlay.Models.Context;
using RemotePlay.Models.DB;
using RemotePlay.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace RemotePlay.Services.Auth
{
    /// <summary>
    /// 认证服务实现
    /// </summary>
    public class AuthService : IAuthService
    {
        private readonly RPContext _context;
        private readonly IConfiguration _configuration;
        private readonly IdGenerator _idGen ;
        private readonly ILogger<AuthService> _logger;
        private const int Pbkdf2Iterations = 1000;

        public AuthService(
            RPContext context,
            IConfiguration configuration,
            ILogger<AuthService> logger)
        {
            _context = context;
            _configuration = configuration;
            _idGen = new IdGenerator(0,0);
            _logger = logger;
        }

        /// <summary>
        /// 注册新用户
        /// </summary>
        public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
        {
            // 检查用户名是否已存在
            if (await _context.Users.AnyAsync(u => u.Username == request.Username))
            {
                throw new InvalidOperationException("用户名已存在");
            }

            // 检查邮箱是否已存在
            if (await _context.Users.AnyAsync(u => u.Email == request.Email))
            {
                throw new InvalidOperationException("邮箱已被注册");
            }

            // 创建新用户
            var user = new User
            {
                Id = _idGen.NextStringId(),
                Username = request.Username,
                Email = request.Email,
                PasswordHash = GeneratePassword(request.Password),
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("新用户注册成功: {Username}, Email: {Email}", user.Username, user.Email);

            // 生成JWT令牌
            var token = GenerateJwtToken(user);
            var expiresAt = DateTime.UtcNow.AddHours(24); // 默认24小时过期

            return new AuthResponse
            {
                Token = token,
                Username = user.Username,
                Email = user.Email,
                ExpiresAt = expiresAt
            };
        }

        /// <summary>
        /// 用户登录
        /// </summary>
        public async Task<AuthResponse?> LoginAsync(LoginRequest request)
        {
            var user = await FindUserByUsernameOrEmailAsync(request.UsernameOrEmail);

            if (user == null || !VerifyHashedPassword(user.PasswordHash,request.Password))
            {
                _logger.LogWarning("登录失败: 用户名或密码错误 - {UsernameOrEmail}", request.UsernameOrEmail);
                return null;
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("登录失败: 用户已被禁用 - {Username}", user.Username);
                return null;
            }

            // 更新最后登录时间
            user.LastLoginAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("用户登录成功: {Username}", user.Username);

            // 生成JWT令牌
            var token = GenerateJwtToken(user);
            var expiresAt = DateTime.UtcNow.AddHours(24); // 默认24小时过期

            return new AuthResponse
            {
                Token = token,
                Username = user.Username,
                Email = user.Email,
                ExpiresAt = expiresAt
            };
        }

        /// <summary>
        /// 验证JWT令牌
        /// </summary>
        public async Task<User?> ValidateTokenAsync(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var key = Encoding.UTF8.GetBytes(GetJwtSecret());

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = true,
                    ValidIssuer = GetJwtIssuer(),
                    ValidateAudience = true,
                    ValidAudience = GetJwtAudience(),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };

                var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

                var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return null;
                }

                return await _context.Users.FindAsync(userIdClaim);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JWT令牌验证失败");
                return null;
            }
        }

        /// <summary>
        /// 根据用户名或邮箱查找用户
        /// </summary>
        public async Task<User?> FindUserByUsernameOrEmailAsync(string usernameOrEmail)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Username == usernameOrEmail || u.Email == usernameOrEmail);
        }

        /// <summary>
        /// 根据用户ID查找用户
        /// </summary>
        public async Task<User?> FindUserByIdAsync(string userId)
        {
            return await _context.Users.FindAsync(userId);
        }

        public string GeneratePassword(string password)
        {
            return Convert.ToBase64String(HashPasswordV3(password, RandomNumberGenerator.Create()
           , prf: KeyDerivationPrf.HMACSHA512, iterCount: Pbkdf2Iterations, saltSize: 128 / 8
           , numBytesRequested: 256 / 8));
        }

        public bool VerifyHashedPassword(string hashedPasswordStr, string password)
        {
            byte[] hashedPassword = Convert.FromBase64String(hashedPasswordStr);
            var iterCount = default(int);
            var prf = default(KeyDerivationPrf);

            try
            {
                prf = (KeyDerivationPrf)ReadNetworkByteOrder(hashedPassword, 1);
                iterCount = (int)ReadNetworkByteOrder(hashedPassword, 5);
                int saltLength = (int)ReadNetworkByteOrder(hashedPassword, 9);

                if (saltLength < 128 / 8)
                {
                    return false;
                }
                byte[] salt = new byte[saltLength];
                Buffer.BlockCopy(hashedPassword, 13, salt, 0, salt.Length);

                int subkeyLength = hashedPassword.Length - 13 - salt.Length;
                if (subkeyLength < 128 / 8)
                {
                    return false;
                }
                byte[] expectedSubkey = new byte[subkeyLength];
                Buffer.BlockCopy(hashedPassword, 13 + salt.Length, expectedSubkey, 0, expectedSubkey.Length);

                byte[] actualSubkey = KeyDerivation.Pbkdf2(password, salt, prf, iterCount, subkeyLength);
                return CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
            }
            catch
            {
                return false;
            }
        }

        private static byte[] HashPasswordV3(string password, RandomNumberGenerator rng, KeyDerivationPrf prf, int iterCount, int saltSize, int numBytesRequested)
        {
            byte[] salt = new byte[saltSize];
            rng.GetBytes(salt);
            byte[] subkey = KeyDerivation.Pbkdf2(password, salt, prf, iterCount, numBytesRequested);
            var outputBytes = new byte[13 + salt.Length + subkey.Length];
            outputBytes[0] = 0x01;
            WriteNetworkByteOrder(outputBytes, 1, (uint)prf);
            WriteNetworkByteOrder(outputBytes, 5, (uint)iterCount);
            WriteNetworkByteOrder(outputBytes, 9, (uint)saltSize);
            Buffer.BlockCopy(salt, 0, outputBytes, 13, salt.Length);
            Buffer.BlockCopy(subkey, 0, outputBytes, 13 + saltSize, subkey.Length);
            return outputBytes;
        }

        private static void WriteNetworkByteOrder(byte[] buffer, int offset, uint value)
        {
            buffer[offset + 0] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)(value >> 0);
        }

        private static uint ReadNetworkByteOrder(byte[] buffer, int offset)
        {
            return ((uint)(buffer[offset + 0]) << 24)
                | ((uint)(buffer[offset + 1]) << 16)
                | ((uint)(buffer[offset + 2]) << 8)
                | ((uint)(buffer[offset + 3]));
        }

        /// <summary>
        /// 生成JWT令牌
        /// </summary>
        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GetJwtSecret()));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: GetJwtIssuer(),
                audience: GetJwtAudience(),
                claims: claims,
                expires: DateTime.UtcNow.AddHours(24),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// 获取JWT密钥
        /// </summary>
        private string GetJwtSecret()
        {
            var secret = _configuration["JWT:Secret"];
            if (string.IsNullOrEmpty(secret))
            {
                // 开发环境默认密钥（生产环境必须配置）
                secret = "YourSuperSecretKeyForJWTTokenGenerationMustBeAtLeast32Characters!";
                _logger.LogWarning("使用默认JWT密钥，生产环境请务必配置JWT:Secret");
            }
            return secret;
        }

        /// <summary>
        /// 获取JWT发行者
        /// </summary>
        private string GetJwtIssuer()
        {
            return _configuration["JWT:Issuer"] ?? "RemotePlay";
        }

        /// <summary>
        /// 获取JWT受众
        /// </summary>
        private string GetJwtAudience()
        {
            return _configuration["JWT:Audience"] ?? "RemotePlayClient";
        }
    }
}

