using Microsoft.Extensions.Logging;
using RemotePlay.Services.Streaming.AV;
using RemotePlay.Services.Streaming.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemotePlay.Services.Streaming.Quality
{
    /// <summary>
    /// 自适应流管理器 - 管理多个视频 Profile 并检测切换
    /// </summary>
    public class AdaptiveStreamManager
    {
        private readonly ILogger<AdaptiveStreamManager> _logger;
        private readonly List<VideoProfile> _profiles = new();
        private int _currentProfileIndex = -1; // -1 表示未初始化
        private readonly object _lock = new();

        public AdaptiveStreamManager(ILogger<AdaptiveStreamManager> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 从 STREAMINFO 设置多个 profiles
        /// </summary>
        public void SetProfiles(IEnumerable<VideoProfile> profiles)
        {
            lock (_lock)
            {
                _profiles.Clear();
                _profiles.AddRange(profiles);
                _currentProfileIndex = -1;

                _logger.LogInformation("📹 AdaptiveStreamManager: set {Count} profiles", _profiles.Count);
                for (int i = 0; i < _profiles.Count; i++)
                {
                    var p = _profiles[i];
                    _logger.LogInformation("  Profile[{Index}]: {Width}x{Height}", i, p.Width, p.Height);
                }
            }
        }

        /// <summary>
        /// 检测并处理 adaptive_stream_index 变化
        /// 返回 (是否切换, 新 Profile, 是否需要更新 Header)
        /// </summary>
        public (bool Switched, VideoProfile? NewProfile, bool NeedUpdateHeader) CheckAndHandleSwitch(AVPacket packet, Action<VideoProfile, VideoProfile?>? onProfileSwitch = null)
        {
            if (packet.Type != HeaderType.VIDEO)
                return (false, null, false);

            lock (_lock)
            {
                int packetIndex = packet.AdaptiveStreamIndex;

                // 首次初始化
                if (_currentProfileIndex < 0)
                {
                    if (packetIndex >= 0 && packetIndex < _profiles.Count)
                    {
                        _currentProfileIndex = packetIndex;
                        var profile = _profiles[_currentProfileIndex];
                        _logger.LogInformation("📹 AdaptiveStreamManager: initialized Profile[{Index}]: {Width}x{Height}", 
                            packetIndex, profile.Width, profile.Height);
                        return (true, profile, true);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ AdaptiveStreamManager: received invalid adaptive_stream_index={Index}, profiles_count={Count}", 
                            packetIndex, _profiles.Count);
                        return (false, null, false);
                    }
                }

                // 检测切换
                if (_currentProfileIndex != packetIndex)
                {
                    if (packetIndex < 0 || packetIndex >= _profiles.Count)
                    {
                        _logger.LogError("❌ AdaptiveStreamManager: received invalid adaptive_stream_index={Index}, profiles_count={Count}", 
                            packetIndex, _profiles.Count);
                        return (false, null, false);
                    }

                    var oldProfile = _profiles[_currentProfileIndex];
                    var newProfile = _profiles[packetIndex];
                    _currentProfileIndex = packetIndex;

                    _logger.LogInformation("🔄 AdaptiveStreamManager: Profile switched {OldIndex}({OldW}x{OldH}) -> {NewIndex}({NewW}x{NewH})",
                        oldProfile.Index, oldProfile.Width, oldProfile.Height,
                        newProfile.Index, newProfile.Width, newProfile.Height);

                    onProfileSwitch?.Invoke(newProfile, oldProfile);
                    return (true, newProfile, true);
                }

                return (false, null, false);
            }
        }

        /// <summary>
        /// 获取当前 Profile
        /// </summary>
        public VideoProfile? GetCurrentProfile()
        {
            lock (_lock)
            {
                if (_currentProfileIndex >= 0 && _currentProfileIndex < _profiles.Count)
                    return _profiles[_currentProfileIndex];
                return null;
            }
        }

        /// <summary>
        /// 获取所有 Profiles
        /// </summary>
        public IReadOnlyList<VideoProfile> GetAllProfiles()
        {
            lock (_lock)
            {
                return _profiles.ToList();
            }
        }

        /// <summary>
        /// 获取 Profile 数量
        /// </summary>
        public int ProfileCount
        {
            get
            {
                lock (_lock)
                {
                    return _profiles.Count;
                }
            }
        }

        /// <summary>
        /// 重置（用于流重置）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentProfileIndex = -1;
                _logger.LogDebug("AdaptiveStreamManager: reset");
            }
        }
    }
}

