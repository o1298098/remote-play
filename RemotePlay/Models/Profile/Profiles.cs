namespace RemotePlay.Models.Profile
{
    /// <summary>
    /// Collection of User Profiles
    /// </summary>
    public class Profiles
    {
        private static string _defaultPath = string.Empty;
        private readonly Dictionary<string, Dictionary<string, object>> _data;

        public Profiles() : this(new Dictionary<string, Dictionary<string, object>>())
        {
        }

        public Profiles(Dictionary<string, Dictionary<string, object>> data)
        {
            _data = data ?? new Dictionary<string, Dictionary<string, object>>();
        }

        /// <summary>
        /// Set default path for loading and saving
        /// </summary>
        /// <param name="path">Path to file</param>
        public static void SetDefaultPath(string path)
        {
            _defaultPath = path ?? string.Empty;
        }

        /// <summary>
        /// Get default path
        /// </summary>
        public static string DefaultPath => _defaultPath;

        /// <summary>
        /// Update stored User Profile
        /// </summary>
        /// <param name="userProfile">User Profile</param>
        public void UpdateUser(UserProfile userProfile)
        {
            if (userProfile == null)
                throw new ArgumentNullException(nameof(userProfile));

            _data[userProfile.Name] = userProfile.Data;
        }

        /// <summary>
        /// Update host in User Profile
        /// </summary>
        /// <param name="userProfile">User Profile</param>
        /// <param name="hostProfile">Host Profile</param>
        public void UpdateHost(UserProfile userProfile, HostProfile hostProfile)
        {
            if (userProfile == null)
                throw new ArgumentNullException(nameof(userProfile));
            if (hostProfile == null)
                throw new ArgumentNullException(nameof(hostProfile));

            userProfile.UpdateHost(hostProfile);
            UpdateUser(userProfile);
        }

        /// <summary>
        /// Remove user
        /// </summary>
        /// <param name="user">User profile or user name to remove</param>
        public void RemoveUser(string user)
        {
            if (string.IsNullOrEmpty(user))
                return;

            _data.Remove(user);
        }

        /// <summary>
        /// Remove user
        /// </summary>
        /// <param name="userProfile">User profile to remove</param>
        public void RemoveUser(UserProfile userProfile)
        {
            if (userProfile == null)
                return;

            RemoveUser(userProfile.Name);
        }

        /// <summary>
        /// Get User Profile for user
        /// </summary>
        /// <param name="userName">PSN ID / Username</param>
        /// <returns>User Profile or null if not found</returns>
        public UserProfile? GetUserProfile(string userName)
        {
            if (string.IsNullOrEmpty(userName))
                return null;

            return Users.FirstOrDefault(u => u.Name == userName);
        }

        /// <summary>
        /// Return all users that are registered with a device
        /// </summary>
        /// <param name="deviceId">Device ID / Device Mac Address</param>
        /// <returns>List of usernames</returns>
        public List<string> GetUsers(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return new List<string>();

            var result = new List<string>();
            foreach (var user in Users)
            {
                if (user.Hosts.Any(h => h.Name == deviceId))
                {
                    result.Add(user.Name);
                }
            }

            return result;
        }

        /// <summary>
        /// List of user names
        /// </summary>
        public List<string> Usernames => _data.Keys.ToList();

        /// <summary>
        /// User Profiles
        /// </summary>
        public List<UserProfile> Users
        {
            get
            {
                var result = new List<UserProfile>();
                foreach (var kvp in _data)
                {
                    try
                    {
                        result.Add(new UserProfile(kvp.Key, kvp.Value));
                    }
                    catch (Exception)
                    {
                        // Skip invalid profiles
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Get raw data
        /// </summary>
        public Dictionary<string, Dictionary<string, object>> Data => new Dictionary<string, Dictionary<string, object>>(_data);
    }
}

