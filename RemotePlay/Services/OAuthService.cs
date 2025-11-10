using RemotePlay.Contracts.Services;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace RemotePlay.Services
{
    /// <summary>
    /// OAuth Service for PSN Authentication
    /// </summary>
    public class OAuthService : IOAuthService
    {
        private readonly ILogger<OAuthService> _logger;
        private readonly HttpClient _httpClient;

        // PSN OAuth constants
        private const string CLIENT_ID = "ba495a24-818c-472b-b12d-ff231c1b5745";
        private const string CLIENT_SECRET = "bXZhaVprUnNBc0kxSUJrWQ=="; // base64 encoded
        private const string REDIRECT_URI = "https://remoteplay.dl.playstation.net/remoteplay/redirect";
        private const string TOKEN_URL = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";

        public OAuthService(ILogger<OAuthService> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        /// <summary>
        /// Get PSN login URL for user authentication
        /// </summary>
        /// <param name="redirectUri">Optional custom redirect URI. If not provided, uses default PSN redirect URI.</param>
        /// <returns>Login URL</returns>
        public string GetLoginUrl(string? redirectUri = null)
        {
            // Use custom redirect URI if provided, otherwise use default
            var redirectUriToUse = redirectUri ?? REDIRECT_URI;
            
            // Build the complete login URL with all required parameters from Python version
            var url = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize" +
                     "?service_entity=urn:service-entity:psn" +
                     "&response_type=code" +
                     $"&client_id={CLIENT_ID}" +
                     $"&redirect_uri={redirectUriToUse}" +
                     "&scope=psn:clientapp" +
                     "&request_locale=en_US" +
                     "&ui=pr" +
                     "&service_logo=ps" +
                     "&layout_type=popup" +
                     "&smcid=remoteplay" +
                     "&prompt=always" +
                     "&PlatformPrivacyWs1=minimal" +
                     "&no_captcha=true";

            _logger.LogInformation("Generated PSN login URL with redirect_uri: {RedirectUri}", redirectUriToUse);
            return url;
        }

        /// <summary>
        /// Get user account information from redirect URL
        /// </summary>
        /// <param name="redirectUrl">URL from signing in with PSN account at the login url</param>
        /// <returns>User account data including user_rpid and online_id</returns>
        public async Task<Dictionary<string, string>?> GetUserAccountAsync(string redirectUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(redirectUrl))
                {
                    _logger.LogError("Redirect URL is empty");
                    return null;
                }

                // Validate redirect URL - 允许我们的前端回调URL或默认PSN回调URL
                var isValidRedirect = redirectUrl.StartsWith(REDIRECT_URI) || 
                                     redirectUrl.Contains("/oauth/callback") ||
                                     redirectUrl.Contains("code=");
                
                if (!isValidRedirect)
                {
                    _logger.LogWarning("Redirect URL may not be valid: {RedirectUrl}", redirectUrl);
                    // 不直接返回null，尝试继续处理
                }

                // Extract code from redirect URL
                var uri = new Uri(redirectUrl);
                var queryString = uri.Query.TrimStart('?');
                var queryParams = queryString.Split('&')
                    .Select(q => q.Split('='))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts[1]));

                queryParams.TryGetValue("code", out var code);

                if (string.IsNullOrEmpty(code) || code.Length <= 1)
                {
                    _logger.LogError("Authorization code not found or invalid in redirect URL");
                    return null;
                }

                _logger.LogDebug("Got Auth Code: {Code}", code);

                // Exchange code for access token with Basic Authentication
                var tokenData = new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", REDIRECT_URI }
                };

                var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TOKEN_URL)
                {
                    Content = new FormUrlEncodedContent(tokenData)
                };

                // Add Basic Authentication header
                var clientSecret = Encoding.UTF8.GetString(Convert.FromBase64String(CLIENT_SECRET));
                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{CLIENT_ID}:{clientSecret}"));
                tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);
                tokenRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

                _logger.LogDebug("Sending POST request to get token");
                var tokenResponse = await _httpClient.SendAsync(tokenRequest);
                if (!tokenResponse.IsSuccessStatusCode)
                {
                    var errorContent = await tokenResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to exchange code for token: {StatusCode}, Error: {Error}",
                        tokenResponse.StatusCode, errorContent);
                    return null;
                }

                var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                var tokenResult = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(tokenJson);

                if (tokenResult == null || !tokenResult.TryGetValue("access_token", out var accessTokenObj))
                {
                    _logger.LogError("Access token not found in response");
                    return null;
                }

                var accessToken = accessTokenObj?.ToString();
                if (string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogError("Access token is empty");
                    return null;
                }

                _logger.LogInformation("Successfully obtained access token");

                // Get user account information using token endpoint (same as Python version)
                var accountUrl = $"{TOKEN_URL}/{accessToken}";
                var accountRequest = new HttpRequestMessage(HttpMethod.Get, accountUrl);

                // Add Basic Authentication header (same as token request)
                accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authValue);

                var accountResponse = await _httpClient.SendAsync(accountRequest);
                if (!accountResponse.IsSuccessStatusCode)
                {
                    var errorContent = await accountResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to get user account: {StatusCode}, Error: {Error}",
                        accountResponse.StatusCode, errorContent);
                    return null;
                }

                var accountJson = await accountResponse.Content.ReadAsStringAsync();
                var accountResult = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(accountJson);

                if (accountResult == null)
                {
                    _logger.LogError("Failed to parse account response");
                    return null;
                }

                // Format account info (same as Python _format_account_info)
                var result = FormatAccountInfo(accountResult);

                _logger.LogInformation("Successfully retrieved user account: {OnlineId}",
                    result.GetValueOrDefault("online_id"));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user account from redirect URL");
                return null;
            }
        }

        /// <summary>
        /// Format account info to include user_rpid (base64) and credentials (sha256)
        /// Same as Python _format_account_info
        /// </summary>
        private Dictionary<string, string> FormatAccountInfo(Dictionary<string, object> accountInfo)
        {
            var result = new Dictionary<string, string>();

            // Get user_id from account info
            if (!accountInfo.TryGetValue("user_id", out var userIdObj) || userIdObj == null)
            {
                _logger.LogError("user_id not found in account info");
                return result;
            }

            var userId = userIdObj.ToString();
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogError("user_id is empty");
                return result;
            }

            // Format user_id to base64 (for user_rpid)
            var userB64 = FormatUserId(userId, "base64");
            result["user_rpid"] = userB64;

            // Format user_id to sha256 (for credentials)
            var userCreds = FormatUserId(userId, "sha256");
            result["credentials"] = userCreds;

            // Add online_id if available
            if (accountInfo.TryGetValue("online_id", out var onlineIdObj) && onlineIdObj != null)
            {
                result["online_id"] = onlineIdObj.ToString() ?? string.Empty;
            }

            _logger.LogDebug("Formatted user_rpid: {UserRpid}, credentials: {Credentials}",
                userB64, userCreds);

            return result;
        }

        /// <summary>
        /// Format user id into useable encoding (base64 or sha256)
        /// Same as Python _format_user_id
        /// </summary>
        private string FormatUserId(string userId, string encoding)
        {
            if (string.IsNullOrEmpty(userId))
                return string.Empty;

            _logger.LogDebug("FormatUserId - userId: {UserId}, encoding: {Encoding}", userId, encoding);

            if (encoding == "sha256")
            {
                // SHA256 hash of user_id (from hex string)
                try
                {
                    byte[] userIdBytes = Convert.FromHexString(userId);
                    using var sha256 = SHA256.Create();
                    var hashBytes = sha256.ComputeHash(userIdBytes);
                    return Convert.ToHexString(hashBytes).ToLower();
                }
                catch (FormatException)
                {
                    // Fallback: treat as UTF-8 string
                    _logger.LogWarning("user_id is not hex, treating as UTF-8 string");
                    using var sha256 = SHA256.Create();
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(userId));
                    return Convert.ToHexString(hashBytes).ToLower();
                }
            }
            else if (encoding == "base64")
            {
                // Python: base64.b64encode(bytes.fromhex(user_id)[:8])
                // user_id应该是十六进制字符串（16字节 = 32个字符）
                try
                {
                    _logger.LogDebug("Attempting to parse user_id as hex string, length: {Length}", userId.Length);

                    byte[] userIdBytes = Convert.FromHexString(userId);
                    _logger.LogDebug("Parsed {Count} bytes from hex", userIdBytes.Length);

                    // 取前8字节
                    byte[] first8Bytes = userIdBytes.Take(8).ToArray();
                    _logger.LogDebug("First 8 bytes: {Hex}", Convert.ToHexString(first8Bytes));

                    string result = Convert.ToBase64String(first8Bytes);
                    _logger.LogDebug("Base64 result: {Result}", result);

                    return result;
                }
                catch (FormatException ex)
                {
                    // Fallback: try as decimal number (old behavior)
                    _logger.LogWarning(ex, "user_id is not hex, trying as decimal number");

                    if (long.TryParse(userId, out var userIdLong))
                    {
                        var bytes = BitConverter.GetBytes(userIdLong);
                        if (!BitConverter.IsLittleEndian)
                        {
                            Array.Reverse(bytes);
                        }
                        _logger.LogDebug("Parsed as long: {Long}, bytes: {Bytes}", userIdLong, Convert.ToHexString(bytes));
                        return Convert.ToBase64String(bytes);
                    }
                    else
                    {
                        _logger.LogError("Failed to parse user_id as hex or long: {UserId}", userId);
                        return string.Empty;
                    }
                }
            }

            throw new ArgumentException($"Invalid encoding: {encoding}. Use 'base64' or 'sha256'");
        }
    }
}

