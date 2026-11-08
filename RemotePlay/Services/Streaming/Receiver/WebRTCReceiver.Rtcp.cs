using SIPSorcery.Net;
using System;
using System.Linq;
using System.Net;
using System.Threading;

namespace RemotePlay.Services.Streaming.Receiver
{
    /// <summary>
    /// 浏览器侧视频接收统计（来自 RTCP Receiver Report）
    /// </summary>
    public readonly record struct BrowserVideoReceptionStats(
        double FractionLost,
        int CumulativePacketsLost,
        uint ExtendedHighestSequenceNumber,
        uint JitterRtpUnits,
        DateTime ReceivedAtUtc);

    public sealed partial class WebRTCReceiver
    {
        private static readonly TimeSpan KEYFRAME_REQUEST_COOLDOWN = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan RTCP_SUMMARY_INTERVAL = TimeSpan.FromSeconds(10);
        private readonly object _rtcpLock = new();
        private DateTime _lastKeyframeRequestTime = DateTime.MinValue;
        private BrowserVideoReceptionStats? _browserVideoStats;
        private bool _rtcpSubscribed;
        private long _rtcpReportCount;
        private long _rtcpKeyframeFeedbackCount;
        private long _rtcpLossKeyframeCount;
        private DateTime _lastRtcpSummaryTime = DateTime.UtcNow;

        /// <summary>
        /// 最近一次浏览器回报的视频接收统计
        /// </summary>
        public BrowserVideoReceptionStats? BrowserVideoStats
        {
            get
            {
                lock (_rtcpLock)
                {
                    return _browserVideoStats;
                }
            }
        }

        private void SubscribeRtcpFeedback()
        {
            if (_peerConnection == null || _rtcpSubscribed)
            {
                return;
            }

            _peerConnection.OnReceiveReport += OnRtcpReportReceived;
            _rtcpSubscribed = true;
        }

        private void UnsubscribeRtcpFeedback()
        {
            if (_peerConnection == null || !_rtcpSubscribed)
            {
                return;
            }

            _peerConnection.OnReceiveReport -= OnRtcpReportReceived;
            _rtcpSubscribed = false;
        }

        private void OnRtcpReportReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, RTCPCompoundPacket packet)
        {
            try
            {
                Interlocked.Increment(ref _rtcpReportCount);

                var feedback = packet.Feedback;
                if (feedback != null &&
                    feedback.Header.PacketType == RTCPReportTypesEnum.PSFB &&
                    (feedback.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI ||
                     feedback.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.FIR))
                {
                    Interlocked.Increment(ref _rtcpKeyframeFeedbackCount);
                    RequestKeyframeFromFeedback(feedback.Header.PayloadFeedbackMessageType.ToString());
                }

                if (mediaType == SDPMediaTypesEnum.video)
                {
                    var reports = packet.ReceiverReport?.ReceptionReports ?? packet.SenderReport?.ReceptionReports;
                    if (reports != null && reports.Count > 0)
                    {
                        var report = reports[0];
                        bool newLoss;
                        lock (_rtcpLock)
                        {
                            newLoss = _browserVideoStats.HasValue &&
                                report.PacketsLost > _browserVideoStats.Value.CumulativePacketsLost;
                            _browserVideoStats = new BrowserVideoReceptionStats(
                                report.FractionLost / 256.0,
                                report.PacketsLost,
                                report.ExtendedHighestSequenceNumber,
                                report.Jitter,
                                DateTime.UtcNow);
                        }

                        // 服务端不支持重传，浏览器丢了任何一个包都只能靠关键帧恢复；不依赖 PLI 是否被正确识别
                        if (newLoss)
                        {
                            Interlocked.Increment(ref _rtcpLossKeyframeCount);
                            RequestKeyframeFromFeedback("RR-loss");
                        }
                    }
                }

                LogRtcpSummaryIfDue();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "⚠️ Exception while processing RTCP report");
            }
        }

        /// <summary>
        /// 记录浏览器 Answer 中协商到的 RTCP 反馈类型（没有 "nack pli" 时浏览器不会发送 PLI）
        /// </summary>
        private void LogNegotiatedRtcpFeedback()
        {
            try
            {
                var sdp = _peerConnection?.remoteDescription?.sdp?.ToString();
                if (string.IsNullOrEmpty(sdp))
                {
                    return;
                }

                var feedbackLines = sdp.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("a=rtcp-fb:", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _logger.LogDebug("Browser-negotiated RTCP feedback: {Lines}",
                    feedbackLines.Count > 0 ? string.Join(" | ", feedbackLines) : "(none)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read negotiated RTCP feedback");
            }
        }

        private string GetRtcpDiagnostics()
        {
            BrowserVideoReceptionStats? stats;
            lock (_rtcpLock)
            {
                stats = _browserVideoStats;
            }

            return $"| RTCPReports={Interlocked.Read(ref _rtcpReportCount)} PLI/FIR={Interlocked.Read(ref _rtcpKeyframeFeedbackCount)} " +
                $"RRLossTriggers={Interlocked.Read(ref _rtcpLossKeyframeCount)} BrowserTotalLost={stats?.CumulativePacketsLost.ToString() ?? "noRR"}";
        }

        private void LogRtcpSummaryIfDue()
        {
            var now = DateTime.UtcNow;
            BrowserVideoReceptionStats? stats;
            lock (_rtcpLock)
            {
                if (now - _lastRtcpSummaryTime < RTCP_SUMMARY_INTERVAL)
                {
                    return;
                }
                _lastRtcpSummaryTime = now;
                stats = _browserVideoStats;
            }

            _logger.LogDebug("RTCP stats: reports={Reports}, PLI/FIR={Pli}, loss-triggered keyframes={LossKeyframes}, browser total lost={Lost}, recent loss rate={Fraction:P1}",
                Interlocked.Read(ref _rtcpReportCount),
                Interlocked.Read(ref _rtcpKeyframeFeedbackCount),
                Interlocked.Read(ref _rtcpLossKeyframeCount),
                stats?.CumulativePacketsLost ?? 0,
                stats?.FractionLost ?? 0);
        }

        private void RequestKeyframeFromFeedback(string source)
        {
            lock (_rtcpLock)
            {
                var now = DateTime.UtcNow;
                if (_lastKeyframeRequestTime != DateTime.MinValue &&
                    (now - _lastKeyframeRequestTime) < KEYFRAME_REQUEST_COOLDOWN)
                {
                    return;
                }

                _lastKeyframeRequestTime = now;
            }

            _logger.LogDebug("🎯 Received browser RTCP {Source}, requesting keyframe", source);
            OnKeyframeRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
