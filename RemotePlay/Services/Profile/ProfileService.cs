using RemotePlay.Contracts.Services;
using RemotePlay.Models.Profile;
using System.Text.Json;

namespace RemotePlay.Services.Profile
{
    /// <summary>
    /// Profile Service for managing PSN user profiles and host profiles
    /// </summary>
    public class ProfileService : IProfileService
    {
        private readonly ILogger<ProfileService> _logger;
        private readonly IOAuthService _oauthService;

        public ProfileService(ILogger<ProfileService> logger, IOAuthService oauthService)
        {
            _logger = logger;
            _oauthService = oauthService;
        }

        /// <summary>
        /// Load profiles from file
        /// </summary>
        /// <param name="path">Path to file. If empty, uses default path</param>
        /// <returns>Profiles collection</returns>
        public async Task<Profiles> LoadAsync(string path = "")
        {
            try
            {
                var filePath = string.IsNullOrEmpty(path) ? Profiles.DefaultPath : path;

                // If still no path, use a default location
                if (string.IsNullOrEmpty(filePath))
                {
                    var defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var appDataDir = Path.Combine(defaultDir, ".remoteplay");
                    filePath = Path.Combine(appDataDir, "profiles.json");

                    _logger.LogInformation("No path specified, using default: {FilePath}", filePath);
                }

                if (!File.Exists(filePath))
                {
                    _logger.LogInformation("Profile file does not exist, creating new file: {Path}", filePath);
                    var emptyProfiles = new Profiles();
                    await SaveAsync(emptyProfiles, filePath);
                    return emptyProfiles;
                }

                var json = await File.ReadAllTextAsync(filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json);

                if (data == null)
                {
                    _logger.LogWarning("Failed to deserialize profiles from {Path}, returning empty profiles", filePath);
                    return new Profiles();
                }

                _logger.LogInformation("Successfully loaded profiles from {Path}", filePath);
                return new Profiles(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading profiles from {Path}", path);
                return new Profiles();
            }
        }

        /// <summary>
        /// Save profiles to file
        /// </summary>
        /// <param name="profiles">Profiles to save</param>
        /// <param name="path">Path to file. If empty, uses default path</param>
        public async Task SaveAsync(Profiles profiles, string path = "")
        {
            try
            {
                if (profiles == null)
                    throw new ArgumentNullException(nameof(profiles));

                var filePath = string.IsNullOrEmpty(path) ? Profiles.DefaultPath : path;

                // If still no path, use a default location
                if (string.IsNullOrEmpty(filePath))
                {
                    // Use a default path in the user's home directory or current directory
                    var defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var appDataDir = Path.Combine(defaultDir, ".remoteplay");
                    filePath = Path.Combine(appDataDir, "profiles.json");

                    _logger.LogInformation("No path specified, using default: {FilePath}", filePath);
                }

                // Create directory if it doesn't exist
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation("Created directory: {Directory}", directory);
                }

                var json = JsonSerializer.Serialize(profiles.Data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(filePath, json);
                _logger.LogInformation("Successfully saved profiles to {Path}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving profiles to {Path}", path);
                throw;
            }
        }

        /// <summary>
        /// Create new PSN user from OAuth redirect URL
        /// </summary>
        /// <param name="profiles">Profiles collection to update</param>
        /// <param name="redirectUrl">URL from signing in with PSN account at the login url</param>
        /// <param name="save">Save profiles to file if true</param>
        /// <returns>Created user profile or null if failed</returns>
        public async Task<UserProfile?> NewUserAsync(Profiles profiles, string redirectUrl, bool save = true)
        {
            try
            {
                if (profiles == null)
                    throw new ArgumentNullException(nameof(profiles));

                if (string.IsNullOrEmpty(redirectUrl))
                {
                    _logger.LogError("Redirect URL is empty");
                    return null;
                }

                _logger.LogInformation("Creating new user from redirect URL");

                var accountData = await _oauthService.GetUserAccountAsync(redirectUrl);
                if (accountData == null)
                {
                    _logger.LogError("Failed to get user account data");
                    return null;
                }

                var profile = FormatUserAccount(accountData);
                if (profile == null)
                {
                    _logger.LogError("Failed to format user account");
                    return null;
                }

                profiles.UpdateUser(profile);

                if (save)
                {
                    await SaveAsync(profiles);
                }

                _logger.LogInformation("Successfully created new user: {Username}", profile.Name);
                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new user");
                return null;
            }
        }

        /// <summary>
        /// Format user account data to user profile
        /// </summary>
        /// <param name="userData">User data from OAuth</param>
        /// <returns>User profile or null if invalid</returns>
        public UserProfile? FormatUserAccount(Dictionary<string, string> userData)
        {
            try
            {
                if (userData == null)
                {
                    _logger.LogError("User data is null");
                    return null;
                }

                if (!userData.TryGetValue("user_rpid", out var userId) || string.IsNullOrEmpty(userId))
                {
                    _logger.LogError("Invalid user id or user id not found");
                    return null;
                }

                if (!userData.TryGetValue("online_id", out var name) || string.IsNullOrEmpty(name))
                {
                    _logger.LogError("Online ID not found");
                    return null;
                }

                // Get credentials (SHA256) - required for device registration
                userData.TryGetValue("credentials", out var credentials);

                var data = new Dictionary<string, object>
                {
                    { "id", userId },
                    { "credentials", credentials ?? string.Empty },  // Add credentials field
                    { "hosts", new Dictionary<string, object>() }
                };

                _logger.LogInformation("Created user profile with credentials for device registration");
                return new UserProfile(name, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error formatting user account");
                return null;
            }
        }

        /// <summary>
        /// Get PSN login URL
        /// </summary>
        /// <param name="redirectUri">Optional custom redirect URI</param>
        /// <returns>Login URL for PSN authentication</returns>
        public string GetLoginUrl(string? redirectUri = null)
        {
            return _oauthService.GetLoginUrl(redirectUri);
        }
    }
}

