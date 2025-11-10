namespace RemotePlay.Models.Profile
{
    /// <summary>
    /// Host Profile for User
    /// </summary>
    public class HostProfile
    {
        private readonly string _name;
        private readonly string _type;
        private readonly Dictionary<string, string> _data;

        public HostProfile(string name, Dictionary<string, string> data, string type)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name must be a non-blank string", nameof(name));

            _name = name;
            _type = type ?? throw new ArgumentNullException(nameof(type));
            _data = data ?? throw new ArgumentNullException(nameof(data));

            Verify();
        }

        private void Verify()
        {
            if (string.IsNullOrEmpty(Name))
                throw new InvalidOperationException("Attribute 'Name' cannot be empty");
            if (string.IsNullOrEmpty(RegistKey))
                throw new InvalidOperationException("Attribute 'RegistKey' cannot be empty");
            if (string.IsNullOrEmpty(RpKey))
                throw new InvalidOperationException("Attribute 'RpKey' cannot be empty");
        }

        /// <summary>
        /// Name / Mac Address
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Host Type (PS4 or PS5)
        /// </summary>
        public string Type => _type;

        /// <summary>
        /// Registration Key
        /// </summary>
        public string RegistKey => _data.TryGetValue("RegistKey", out var value) ? value : string.Empty;

        /// <summary>
        /// Remote Play Key
        /// </summary>
        public string RpKey => _data.TryGetValue("RP-Key", out var value) ? value : string.Empty;

        /// <summary>
        /// Get all data as dictionary
        /// </summary>
        public Dictionary<string, string> Data => new Dictionary<string, string>(_data);

        /// <summary>
        /// Get data by key
        /// </summary>
        public string this[string key]
        {
            get => _data.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }
}

