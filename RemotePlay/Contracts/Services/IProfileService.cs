using RemotePlay.Models.Profile;

namespace RemotePlay.Contracts.Services
{
    /// <summary>
    /// Profile Service for managing PSN user profiles and host profiles
    /// </summary>
    public interface IProfileService
    {
        /// <summary>
        /// Load profiles from file
        /// </summary>
        /// <param name="path">Path to file. If empty, uses default path</param>
        /// <returns>Profiles collection</returns>
        Task<Profiles> LoadAsync(string path = "");

        /// <summary>
        /// Save profiles to file
        /// </summary>
        /// <param name="profiles">Profiles to save</param>
        /// <param name="path">Path to file. If empty, uses default path</param>
        Task SaveAsync(Profiles profiles, string path = "");

        /// <summary>
        /// Create new PSN user from OAuth redirect URL
        /// </summary>
        /// <param name="profiles">Profiles collection to update</param>
        /// <param name="redirectUrl">URL from signing in with PSN account at the login url</param>
        /// <param name="save">Save profiles to file if true</param>
        /// <returns>Created user profile or null if failed</returns>
        Task<UserProfile?> NewUserAsync(Profiles profiles, string redirectUrl, bool save = true);

        /// <summary>
        /// Format user account data to user profile
        /// </summary>
        /// <param name="userData">User data from OAuth</param>
        /// <returns>User profile or null if invalid</returns>
        UserProfile? FormatUserAccount(Dictionary<string, string> userData);

        /// <summary>
        /// Get PSN login URL
        /// </summary>
        /// <param name="redirectUri">Optional custom redirect URI</param>
        /// <returns>Login URL for PSN authentication</returns>
        string GetLoginUrl(string? redirectUri = null);
    }
}

