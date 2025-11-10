namespace RemotePlay.Models.Profile
{
    /// <summary>
    /// PSN User Profile. Stores Host Profiles for user.
    /// </summary>
    public class UserProfile
    {
        private readonly string _name;
        private readonly Dictionary<string, object> _data;

        public UserProfile(string name, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name must be a non-blank string", nameof(name));

            _name = name;
            _data = data ?? throw new ArgumentNullException(nameof(data));

            Verify();
        }

        private void Verify()
        {
            if (string.IsNullOrEmpty(Name))
                throw new InvalidOperationException("Attribute 'Name' cannot be empty");
            if (string.IsNullOrEmpty(Id))
                throw new InvalidOperationException("Attribute 'Id' cannot be empty");
        }

        /// <summary>
        /// Update host profile
        /// </summary>
        /// <param name="hostProfile">Host Profile</param>
        public void UpdateHost(HostProfile hostProfile)
        {
            if (hostProfile == null)
                throw new ArgumentNullException(nameof(hostProfile));

            var hosts = GetHostsData();
            var hostData = new Dictionary<string, object>
            {
                { "type", hostProfile.Type },
                { "data", hostProfile.Data }
            };
            hosts[hostProfile.Name] = hostData;
            _data["hosts"] = hosts;
        }

        /// <summary>
        /// Add registration data to user profile
        /// </summary>
        /// <param name="hostStatus">Status from device</param>
        /// <param name="registData">Data from registering</param>
        public void AddRegistData(Dictionary<string, string> hostStatus, Dictionary<string, string> registData)
        {
            if (hostStatus == null)
                throw new ArgumentNullException(nameof(hostStatus));
            if (registData == null)
                throw new ArgumentNullException(nameof(registData));

            var hostId = hostStatus.TryGetValue("host-id", out var hid) ? hid : string.Empty;
            var hostType = hostStatus.TryGetValue("host-type", out var htype) ? htype : "PS5";

            if (string.IsNullOrEmpty(hostId))
                throw new ArgumentException("host-id is required in hostStatus");

            var hosts = GetHostsData();
            var hostData = new Dictionary<string, object>
            {
                { "type", hostType },
                { "data", new Dictionary<string, string>(registData) }
            };
            hosts[hostId] = hostData;
            _data["hosts"] = hosts;
        }

        private Dictionary<string, object> GetHostsData()
        {
            if (!_data.ContainsKey("hosts"))
                _data["hosts"] = new Dictionary<string, object>();

            return _data["hosts"] as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// PSN Username
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Base64 encoded User ID (user_rpid)
        /// </summary>
        public string Id => _data.TryGetValue("id", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

        /// <summary>
        /// SHA256 credentials for device registration
        /// </summary>
        public string Credentials => _data.TryGetValue("credentials", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

        /// <summary>
        /// Host profiles
        /// </summary>
        public List<HostProfile> Hosts
        {
            get
            {
                var hosts = GetHostsData();
                var result = new List<HostProfile>();

                foreach (var kvp in hosts)
                {
                    if (kvp.Value is Dictionary<string, object> hostObj)
                    {
                        var type = hostObj.TryGetValue("type", out var t) ? t?.ToString() ?? "PS5" : "PS5";
                        var data = hostObj.TryGetValue("data", out var d) && d is Dictionary<string, string> dict
                            ? dict
                            : new Dictionary<string, string>();

                        result.Add(new HostProfile(kvp.Key, data, type));
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Get user data as dictionary
        /// </summary>
        public Dictionary<string, object> Data => new Dictionary<string, object>(_data);
    }
}

