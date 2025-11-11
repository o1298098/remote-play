using SIPSorcery.Net;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace RemotePlay.Services.Streaming.Receiver
{
    public sealed partial class WebRTCReceiver
    {
        /// <summary>
        /// 初始化 RTCP 反馈监听（用于自动感知关键帧请求）
        /// </summary>
        private void InitializeRTCPFeedback()
        {
            try
            {
                if (_peerConnection == null) return;

                var attached = TryAttachRtcpFeedbackHandlers(_peerConnection, "RTCPeerConnection");
                if (attached)
                {
                    _logger.LogInformation("✅ 已在 RTCPeerConnection 上订阅 RTCP 反馈事件");
                }
                else
                {
                    _logger.LogDebug("ℹ️ 未在 RTCPeerConnection 上找到可用的 RTCP 反馈事件，将在 RTP 会话准备后继续尝试");
                }

                _logger.LogInformation("📡 RTCP 反馈监听初始化完成（等待 RTP 会话就绪）");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 初始化 RTCP 反馈监听失败，将无法自动感知关键帧请求");
            }
        }

        private void InitializeRtpChannels()
        {
            try
            {
                if (_peerConnection == null || _videoTrack == null) return;

                ActivateRTCPFeedback();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 初始化 RTP 通道失败");
            }
        }

        /// <summary>
        /// 激活 RTCP 反馈监听（在连接建立后调用）
        /// </summary>
        private void ActivateRTCPFeedback()
        {
            try
            {
                if (_peerConnection == null) return;

                var attachedAny = false;

                if (_videoTrack != null)
                {
                    attachedAny |= TryAttachRtcpFeedbackFromTrack(_videoTrack, "VideoTrack");
                }

                if (!attachedAny)
                {
                    attachedAny |= TryAttachRtcpFeedbackFromPeerConnectionInternals();
                }

                if (attachedAny)
                {
                    lock (_rtcpFeedbackLock)
                    {
                        _rtcpFeedbackSubscribed = true;
                    }
                    _logger.LogInformation("📡 RTCP 反馈监听已激活（将自动响应浏览器 PLI/FIR）");
                }
                else
                {
                    _logger.LogWarning("⚠️ 未找到可订阅的 RTCP 反馈事件，将继续依赖连接状态作为备用方案");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 激活 RTCP 反馈监听失败");
            }
        }

        private bool TryAttachRtcpFeedbackFromTrack(MediaStreamTrack track, string sourceLabel)
        {
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var trackType = track.GetType();
            var attached = false;

            var properties = trackType.GetProperties(bindingFlags)
                .Where(p => p.GetIndexParameters().Length == 0 && IsPotentialRtpContainer(p.PropertyType, p.Name))
                .ToList();

            foreach (var property in properties)
            {
                try
                {
                    var value = property.GetValue(track);
                    attached |= AttachToValue(value, $"{sourceLabel}.{property.Name}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("⚠️ 无法访问 {Source}.{Property}: {Message}", sourceLabel, property.Name, ex.Message);
                }
            }

            var fields = trackType.GetFields(bindingFlags)
                .Where(f => IsPotentialRtpContainer(f.FieldType, f.Name))
                .ToList();

            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(track);
                    attached |= AttachToValue(value, $"{sourceLabel}.{field.Name}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("⚠️ 无法访问 {Source}.{Field}: {Message}", sourceLabel, field.Name, ex.Message);
                }
            }

            return attached;
        }

        private bool TryAttachRtcpFeedbackFromPeerConnectionInternals()
        {
            if (_peerConnection == null) return false;

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var peerType = _peerConnection.GetType();
            var attached = false;

            var properties = peerType.GetProperties(bindingFlags)
                .Where(p => p.GetIndexParameters().Length == 0 && IsPotentialRtpContainer(p.PropertyType, p.Name))
                .ToList();

            foreach (var property in properties)
            {
                try
                {
                    var value = property.GetValue(_peerConnection);
                    attached |= AttachToValue(value, $"RTCPeerConnection.{property.Name}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("⚠️ 无法访问 RTCPeerConnection.{Property}: {Message}", property.Name, ex.Message);
                }
            }

            var fields = peerType.GetFields(bindingFlags)
                .Where(f => IsPotentialRtpContainer(f.FieldType, f.Name))
                .ToList();

            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(_peerConnection);
                    attached |= AttachToValue(value, $"RTCPeerConnection.{field.Name}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("⚠️ 无法访问 RTCPeerConnection.{Field}: {Message}", field.Name, ex.Message);
                }
            }

            return attached;
        }

        private bool AttachToValue(object? value, string label)
        {
            if (value == null) return false;

            var attached = false;

            attached |= TryAttachRtcpFeedbackHandlers(value, label);

            if (!attached && value is System.Collections.IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    attached |= TryAttachRtcpFeedbackHandlers(item, $"{label}[]");
                }
            }

            return attached;
        }

        private bool TryAttachRtcpFeedbackHandlers(object target, string source)
        {
            if (target == null) return false;

            var targetType = target.GetType();
            var events = targetType.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(e => IsRtcpFeedbackEvent(e.Name))
                .ToList();

            if (events.Count == 0)
            {
                return false;
            }

            var attached = false;

            foreach (var evt in events)
            {
                var key = $"{targetType.FullName}.{evt.Name}";
                lock (_rtcpFeedbackLock)
                {
                    if (_rtcpSubscribedEventKeys.Contains(key))
                    {
                        continue;
                    }
                }

                try
                {
                    var handler = CreateRtcpFeedbackDelegate(evt, $"{source}.{evt.Name}");
                    if (handler == null)
                    {
                        continue;
                    }

                    evt.AddEventHandler(target, handler);

                    lock (_rtcpFeedbackLock)
                    {
                        _rtcpSubscribedEventKeys.Add(key);
                        _rtcpFeedbackSubscriptions.Add((target, evt, handler));
                    }

                    _logger.LogInformation("✅ 已订阅 RTCP 反馈事件: {Source}", $"{source}.{evt.Name}");
                    attached = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "⚠️ 订阅 RTCP 反馈事件失败: {Source}", $"{source}.{evt.Name}");
                }
            }

            return attached;
        }

        private Delegate? CreateRtcpFeedbackDelegate(EventInfo eventInfo, string sourceTag)
        {
            var handlerType = eventInfo.EventHandlerType;
            if (handlerType == null) return null;

            var invokeMethod = handlerType.GetMethod("Invoke");
            if (invokeMethod == null) return null;

            var parameters = invokeMethod.GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();

            var argsArray = Expression.NewArrayInit(typeof(object),
                parameters.Select(p => Expression.Convert(p, typeof(object))));

            var callbackMethod = typeof(WebRTCReceiver).GetMethod(nameof(HandleRtcpFeedback), BindingFlags.Instance | BindingFlags.NonPublic);
            if (callbackMethod == null)
            {
                return null;
            }

            var callExpression = Expression.Call(
                Expression.Constant(this),
                callbackMethod,
                Expression.Constant(sourceTag, typeof(string)),
                argsArray);

            return Expression.Lambda(handlerType, callExpression, parameters).Compile();
        }

        private static bool IsRtcpFeedbackEvent(string? eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return false;
            }

            var lower = eventName.ToLowerInvariant();

            if (lower.Contains("report"))
            {
                return false;
            }

            return lower.Contains("pli") ||
                   lower.Contains("pictureloss") ||
                   lower.Contains("fullintra") ||
                   lower.Contains("fir") ||
                   lower.Contains("feedback") ||
                   lower.Contains("rtcp") ||
                   lower.Contains("nack");
        }

        private static bool IsPotentialRtpContainer(Type type, string memberName)
        {
            var lowerName = memberName.ToLowerInvariant();
            if (lowerName.Contains("rtp") || lowerName.Contains("session"))
            {
                return true;
            }

            var typeName = type.FullName?.ToLowerInvariant() ?? type.Name.ToLowerInvariant();
            return typeName.Contains("rtp") || typeName.Contains("session");
        }

        private void HandleRtcpFeedback(string source, object?[]? args)
        {
            try
            {
                if (!ShouldTriggerKeyframe(source, args))
                {
                    _logger.LogTrace("ℹ️ 捕获到非关键帧类 RTCP 事件: {Source}", source);
                    return;
                }

                string argsSummary = args == null
                    ? "无参数"
                    : string.Join(", ", args.Select(a => a?.GetType().Name ?? "null"));

                _logger.LogInformation("📥 捕获到浏览器关键帧请求 ({Source})，参数: {Args}", source, argsSummary);
                RequestKeyframeFromFeedback(source);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "⚠️ 处理 RTCP 反馈时发生异常: {Source}", source);
            }
        }

        private static bool ShouldTriggerKeyframe(string source, object?[]? args)
        {
            if (ContainsKeyframeHint(source))
            {
                return true;
            }

            if (args == null)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (arg == null)
                {
                    continue;
                }

                if (ContainsKeyframeHint(arg.GetType().Name))
                {
                    return true;
                }

                var argString = arg.ToString();
                if (!string.IsNullOrEmpty(argString) && ContainsKeyframeHint(argString))
                {
                    return true;
                }

                var argType = arg.GetType();
                var feedbackTypeProperty = argType.GetProperty("FeedbackType") ?? argType.GetProperty("FeedbackMessageType");
                if (feedbackTypeProperty != null)
                {
                    var value = feedbackTypeProperty.GetValue(arg)?.ToString();
                    if (!string.IsNullOrEmpty(value) && ContainsKeyframeHint(value))
                    {
                        return true;
                    }
                }

                var messageTypeProperty = argType.GetProperty("MessageType");
                if (messageTypeProperty != null)
                {
                    var value = messageTypeProperty.GetValue(arg)?.ToString();
                    if (!string.IsNullOrEmpty(value) && ContainsKeyframeHint(value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsKeyframeHint(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lower = text.ToLowerInvariant();
            return lower.Contains("pli") ||
                   lower.Contains("pictureloss") ||
                   lower.Contains("fullintra") ||
                   lower.Contains("fir");
        }

        private void RequestKeyframeFromFeedback(string source)
        {
            lock (_rtcpFeedbackLock)
            {
                var now = DateTime.UtcNow;
                if (_lastKeyframeRequestTime != DateTime.MinValue &&
                    (now - _lastKeyframeRequestTime) < KEYFRAME_REQUEST_COOLDOWN)
                {
                    _logger.LogDebug("⏱️ 忽略过于频繁的关键帧请求: {Source}", source);
                    return;
                }

                _lastKeyframeRequestTime = now;
            }

            _logger.LogInformation("🎯 已根据 RTCP 反馈触发关键帧请求: {Source}", source);
            OnKeyframeRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

