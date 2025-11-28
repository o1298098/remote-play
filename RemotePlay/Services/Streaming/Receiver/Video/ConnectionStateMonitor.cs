using System;
using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 连接状态监控器 - 缓存连接状态，减少属性访问开销
    /// </summary>
    internal class ConnectionStateMonitor
    {
        private readonly RTCPeerConnection? _peerConnection;
        private readonly object _lock = new();
        
        private RTCPeerConnectionState? _cachedConnectionState;
        private RTCIceConnectionState? _cachedIceState;
        private RTCSignalingState? _cachedSignalingState;
        private DateTime _lastStateCheckTime = DateTime.MinValue;
        
        private const int STATE_CACHE_MS = 100; // ✅ 优化：增加缓存时间到100ms，减少频繁检查，适应远程连接

        public ConnectionStateMonitor(RTCPeerConnection? peerConnection)
        {
            _peerConnection = peerConnection;
        }

        /// <summary>
        /// 获取缓存的连接状态（带缓存）
        /// </summary>
        public (RTCPeerConnectionState connectionState, RTCIceConnectionState iceState, RTCSignalingState signalingState) GetCachedState()
        {
            if (_peerConnection == null)
            {
                return (RTCPeerConnectionState.closed, RTCIceConnectionState.closed, RTCSignalingState.closed);
            }

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                
                // 如果缓存过期，更新缓存
                if (_cachedConnectionState == null || 
                    _cachedIceState == null || 
                    _cachedSignalingState == null ||
                    (now - _lastStateCheckTime).TotalMilliseconds > STATE_CACHE_MS)
                {
                    _cachedConnectionState = _peerConnection.connectionState;
                    _cachedIceState = _peerConnection.iceConnectionState;
                    _cachedSignalingState = _peerConnection.signalingState;
                    _lastStateCheckTime = now;
                }
                
                return (_cachedConnectionState.Value, _cachedIceState.Value, _cachedSignalingState.Value);
            }
        }

        /// <summary>
        /// 检查是否允许发送视频（放宽检查，适应远程前端连接）
        /// </summary>
        public bool CanSendVideo()
        {
            var (connectionState, iceState, signalingState) = GetCachedState();
            
            // ✅ 优化：只阻止完全关闭或失败的状态，其他状态都允许尝试发送
            // 这样可以适应远程前端连接可能出现的短暂状态波动
            if (connectionState == RTCPeerConnectionState.closed || 
                connectionState == RTCPeerConnectionState.failed)
            {
                return false;
            }
            
            // ✅ 优化：进一步放宽条件，允许在更多状态下尝试发送
            // 适应远程前端连接可能出现的状态延迟
            return true; // 只要不是 closed 或 failed，都允许尝试发送
        }

        /// <summary>
        /// 清除缓存（强制下次更新）
        /// </summary>
        public void ClearCache()
        {
            lock (_lock)
            {
                _cachedConnectionState = null;
                _cachedIceState = null;
                _cachedSignalingState = null;
                _lastStateCheckTime = DateTime.MinValue;
            }
        }
    }
}

