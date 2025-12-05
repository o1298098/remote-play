using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace RemotePlay.Services.Streaming.Receiver.Video
{
    /// <summary>
    /// 反射方法缓存 - 缓存并管理 WebRTC 发送方法的反射调用
    /// </summary>
    internal class ReflectionMethodCache
    {
        private readonly ILogger? _logger;
        private readonly RTCPeerConnection? _peerConnection;
        private readonly MediaStreamTrack? _videoTrack;
        
        private MethodInfo? _cachedSendVideoMethod;
        private MethodInfo? _cachedSendRtpRawMethod;
        private bool _initialized = false;
        private readonly object _initLock = new();
        
        public ReflectionMethodCache(
            ILogger? logger,
            RTCPeerConnection? peerConnection,
            MediaStreamTrack? videoTrack)
        {
            _logger = logger;
            _peerConnection = peerConnection;
            _videoTrack = videoTrack;
        }
        
        /// <summary>
        /// 初始化反射方法缓存
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            
            lock (_initLock)
            {
                if (_initialized) return;
                
                if (_peerConnection == null) return;
                
                try
                {
                    var peerConnectionType = _peerConnection.GetType();
                    
                    // 查找 SendVideo 方法
                    var sendVideoMethods = peerConnectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "SendVideo")
                        .ToList();
                    
                    if (sendVideoMethods.Count == 0)
                    {
                        var baseType = peerConnectionType.BaseType;
                        if (baseType != null)
                        {
                            sendVideoMethods = baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name == "SendVideo")
                                .ToList();
                        }
                    }
                    
                    foreach (var method in sendVideoMethods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType == typeof(uint) &&
                            parameters[1].ParameterType == typeof(byte[]))
                        {
                            _cachedSendVideoMethod = method;
                            break;
                        }
                    }
                    
                    // 查找 SendRtpRaw 方法（5参数版本，优先）
                    var sendRtpRawMethods = peerConnectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "SendRtpRaw")
                        .ToList();
                    
                    if (sendRtpRawMethods.Count == 0)
                    {
                        var baseType = peerConnectionType.BaseType;
                        if (baseType != null)
                        {
                            sendRtpRawMethods = baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name == "SendRtpRaw")
                                .ToList();
                        }
                    }
                    
                    // 优先选择 5 参数版本
                    foreach (var method in sendRtpRawMethods)
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 5 &&
                            parameters[0].ParameterType == typeof(SDPMediaTypesEnum) &&
                            parameters[1].ParameterType == typeof(byte[]) &&
                            parameters[2].ParameterType == typeof(uint) &&
                            parameters[3].ParameterType == typeof(int) &&
                            parameters[4].ParameterType == typeof(int))
                        {
                            _cachedSendRtpRawMethod = method;
                            break;
                        }
                    }
                    
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "初始化反射方法缓存失败");
                }
            }
        }
        
        /// <summary>
        /// 异步调用 SendVideo 方法
        /// </summary>
        public async Task<bool> InvokeSendVideoAsync(uint timestamp, byte[] data, int timeoutMs = 100, int maxRetries = 1)
        {
            if (_cachedSendVideoMethod == null)
            {
                Initialize();
                if (_cachedSendVideoMethod == null)
                {
                    return false;
                }
            }
            
            for (int retry = 0; retry <= maxRetries; retry++)
            {
                try
                {
                    if (_peerConnection == null) return false;
                    
                    var invokeTask = Task.Run(() =>
                    {
                        try
                        {
                            return _cachedSendVideoMethod.Invoke(_peerConnection, new object[] { timestamp, data });
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("SendVideo failed", ex);
                        }
                    });
                    
                    var timeoutTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(invokeTask, timeoutTask).ConfigureAwait(false);
                    
                    if (completedTask == timeoutTask)
                    {
                        if (retry < maxRetries)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            continue;
                        }
                        _logger?.LogWarning("InvokeSendVideoAsync 超时 ({Timeout}ms, 重试 {Retry}/{MaxRetries})", 
                            timeoutMs, retry + 1, maxRetries + 1);
                        return false;
                    }
                    
                    if (invokeTask.IsFaulted)
                    {
                        var ex = invokeTask.Exception?.InnerException ?? invokeTask.Exception ?? new Exception("SendVideo failed");
                        if (retry < maxRetries)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            continue;
                        }
                        _logger?.LogWarning(ex, "InvokeSendVideoAsync 失败 (重试 {Retry}/{MaxRetries})", retry + 1, maxRetries + 1);
                        return false;
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    if (retry < maxRetries)
                    {
                        await Task.Delay(5).ConfigureAwait(false);
                        continue;
                    }
                    _logger?.LogWarning(ex, "InvokeSendVideoAsync 异常 (重试 {Retry}/{MaxRetries})", retry + 1, maxRetries + 1);
                    return false;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// 异步调用 SendRtpRaw 方法（5参数版本）
        /// </summary>
        public async Task<bool> InvokeSendRtpRawAsync(
            SDPMediaTypesEnum mediaType,
            byte[] rtpData,
            uint timestamp,
            int markerBit,
            int payloadType,
            int timeoutMs = 100,
            int maxRetries = 1)
        {
            if (_cachedSendRtpRawMethod == null)
            {
                Initialize();
                if (_cachedSendRtpRawMethod == null)
                {
                    return false;
                }
            }
            
            for (int retry = 0; retry <= maxRetries; retry++)
            {
                try
                {
                    if (_peerConnection == null) return false;
                    
                    var invokeTask = Task.Run(() =>
                    {
                        try
                        {
                            return _cachedSendRtpRawMethod.Invoke(_peerConnection, new object[] {
                                mediaType, rtpData, timestamp, markerBit, payloadType
                            });
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("SendRtpRaw failed", ex);
                        }
                    });
                    
                    var timeoutTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(invokeTask, timeoutTask).ConfigureAwait(false);
                    
                    if (completedTask == timeoutTask)
                    {
                        if (retry < maxRetries)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            continue;
                        }
                        _logger?.LogWarning("InvokeSendRtpRawAsync 超时 ({Timeout}ms, 重试 {Retry}/{MaxRetries})", 
                            timeoutMs, retry + 1, maxRetries + 1);
                        return false;
                    }
                    
                    if (invokeTask.IsFaulted)
                    {
                        var ex = invokeTask.Exception?.InnerException ?? invokeTask.Exception ?? new Exception("SendRtpRaw failed");
                        if (retry < maxRetries)
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                            continue;
                        }
                        _logger?.LogWarning(ex, "InvokeSendRtpRawAsync 失败 (重试 {Retry}/{MaxRetries})", retry + 1, maxRetries + 1);
                        return false;
                    }
                    
                    return true;
                }
                catch (Exception ex)
                {
                    if (retry < maxRetries)
                    {
                        await Task.Delay(5).ConfigureAwait(false);
                        continue;
                    }
                    _logger?.LogWarning(ex, "InvokeSendRtpRawAsync 异常 (重试 {Retry}/{MaxRetries})", retry + 1, maxRetries + 1);
                    return false;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// 清除缓存（用于重新初始化）
        /// </summary>
        public void Clear()
        {
            lock (_initLock)
            {
                _cachedSendVideoMethod = null;
                _cachedSendRtpRawMethod = null;
                _initialized = false;
            }
        }
    }
}

