namespace RemotePlay.Contracts.Services
{
    /// <summary>
    /// OAuth Service for PSN Authentication
    /// </summary>
    public interface IOAuthService
    {
        /// <summary>
        /// Get PSN login URL for user authentication
        /// </summary>
        /// <param name="redirectUri">Optional custom redirect URI. If not provided, uses default PSN redirect URI.</param>
        /// <returns>Login URL</returns>
        string GetLoginUrl(string? redirectUri = null);

        /// <summary>
        /// Get user account information from redirect URL
        /// </summary>
        /// <param name="redirectUrl">URL from signing in with PSN account at the login url</param>
        /// <returns>User account data including user_rpid and online_id</returns>
        Task<Dictionary<string, string>?> GetUserAccountAsync(string redirectUrl);
    }
}

