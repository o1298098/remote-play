using System.Net;
using SIPSorcery.Net;

namespace RemotePlay.Services.WebRTC
{
    /// <summary>
    /// WebRTCSignalingService SDP 处理部分
    /// </summary>
    public partial class WebRTCSignalingService
    {
        /// <summary>
        /// 为每个视频编码声明 PLI/FIR 反馈，并移除服务端未实现的反馈类型
        /// （generic NACK、goog-remb、transport-cc），避免浏览器等待永远不会到来的重传或带宽估计。
        /// </summary>
        private string OptimizeSdpForLowLatency(string sdp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sdp) || sdp.Length < 10)
                    return sdp;

                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                var videoCodecByPt = new Dictionary<string, string>(StringComparer.Ordinal);
                var existingLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ptsWithFmtp = new HashSet<string>(StringComparer.Ordinal);
                bool inVideo = false;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    existingLines.Add(line);
                    if (line.StartsWith("m=", StringComparison.Ordinal))
                    {
                        inVideo = line.StartsWith("m=video ", StringComparison.Ordinal);
                        continue;
                    }
                    if (!inVideo)
                        continue;

                    if (line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring("a=rtpmap:".Length).Split(' ', 2);
                        if (parts.Length == 2)
                        {
                            var codec = parts[1].Split('/')[0].ToUpperInvariant();
                            if (codec is "H264" or "H265" or "HEVC")
                                videoCodecByPt[parts[0]] = codec;
                        }
                    }
                    else if (line.StartsWith("a=fmtp:", StringComparison.OrdinalIgnoreCase))
                    {
                        ptsWithFmtp.Add(line.Substring("a=fmtp:".Length).Split(' ', 2)[0]);
                    }
                }

                var optimizedLines = new List<string>(lines.Length + videoCodecByPt.Count * 3);
                inVideo = false;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.StartsWith("m=", StringComparison.Ordinal))
                        inVideo = line.StartsWith("m=video ", StringComparison.Ordinal);

                    if (line.StartsWith("a=x-google-flag:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("a=minBufferedPlaybackTime", StringComparison.OrdinalIgnoreCase) ||
                        IsUnsupportedRtcpFeedback(line))
                    {
                        continue;
                    }

                    optimizedLines.Add(raw);

                    if (!inVideo || !line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pt = line.Substring("a=rtpmap:".Length).Split(' ', 2)[0];
                    if (!videoCodecByPt.TryGetValue(pt, out var videoCodec))
                        continue;

                    if (videoCodec == "H264" && !ptsWithFmtp.Contains(pt))
                        optimizedLines.Add($"a=fmtp:{pt} level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f");

                    foreach (var fb in new[] { $"a=rtcp-fb:{pt} nack pli", $"a=rtcp-fb:{pt} ccm fir" })
                    {
                        if (!existingLines.Contains(fb))
                            optimizedLines.Add(fb);
                    }
                }

                var result = string.Join("\r\n", optimizedLines);

                if (!result.Contains("v=0") || !result.Contains("m="))
                {
                    _logger.LogWarning("⚠️ SDP 优化后结构不完整，使用原始 SDP");
                    return sdp;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ SDP 优化失败，使用原始 SDP");
                return sdp;
            }
        }

        private static bool IsUnsupportedRtcpFeedback(string line)
        {
            if (!line.StartsWith("a=rtcp-fb:", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = line.Substring("a=rtcp-fb:".Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            var type = parts[1].ToLowerInvariant();
            return (type == "nack" && parts.Length == 2) || type == "goog-remb" || type == "transport-cc";
        }

        private string ApplyPublicIpToSdp(string sdp, string? publicIp = null)
        {
            // 优先使用传入的 publicIp，否则使用 _config 中的值
            publicIp = publicIp?.Trim() ?? _config.PublicIp?.Trim();
            if (string.IsNullOrWhiteSpace(publicIp))
            {
                return sdp;
            }

            try
            {
                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var updated = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith("c=IN IP", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length >= 3)
                        {
                            parts[2] = publicIp;
                            lines[i] = string.Join(" ", parts);
                            updated = true;
                        }
                    }
                    else if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
                    {
                        var segments = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length > 7 && string.Equals(segments[6], "typ", StringComparison.OrdinalIgnoreCase))
                        {
                            var candidateType = segments[7];
                            if (string.Equals(candidateType, "host", StringComparison.OrdinalIgnoreCase))
                            {
                                segments[4] = publicIp;
                                lines[i] = string.Join(" ", segments);
                                updated = true;
                            }
                        }
                    }
                }

                if (updated)
                {
                    _logger.LogInformation("🌐 已应用 WebRTC PublicIp 配置: {PublicIp}", publicIp);
                    return string.Join("\r\n", lines);
                }

                return sdp;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 应用 PublicIp 到 SDP 失败，使用原始 SDP");
                return sdp;
            }
        }

        private string PrioritizeLanCandidates(string sdp, bool? preferLanCandidatesOverride = null)
        {
            var preferLanCandidates = preferLanCandidatesOverride ?? _config.PreferLanCandidates;

            if (!preferLanCandidates || string.IsNullOrWhiteSpace(sdp))
            {
                return sdp;
            }

            try
            {
                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var optimizedLines = new List<string>(lines.Length);
                var candidateBuffer = new List<(string line, int index)>();
                var collectingCandidates = false;
                var order = 0;

                void FlushBuffer()
                {
                    if (candidateBuffer.Count == 0) return;

                    var sorted = candidateBuffer
                        .Select(entry => new { entry.line, entry.index, score = ScoreCandidate(entry.line) })
                        .OrderByDescending(x => x.score)
                        .ThenBy(x => x.index)
                        .Select(x => x.line);

                    optimizedLines.AddRange(sorted);
                    candidateBuffer.Clear();
                }

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("m=", StringComparison.Ordinal))
                    {
                        FlushBuffer();
                        optimizedLines.Add(line);
                        collectingCandidates = false;
                        continue;
                    }

                    if (trimmed.StartsWith("a=candidate", StringComparison.Ordinal))
                    {
                        collectingCandidates = true;
                        candidateBuffer.Add((line, order++));
                        continue;
                    }

                    if (collectingCandidates && !trimmed.StartsWith("a=candidate", StringComparison.Ordinal))
                    {
                        FlushBuffer();
                        collectingCandidates = false;
                    }

                    optimizedLines.Add(line);
                }

                FlushBuffer();

                return string.Join("\r\n", optimizedLines);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 优化候选地址顺序失败，使用原始 SDP");
                return sdp;
            }
        }

        private int ScoreCandidate(string candidateLine)
        {
            if (string.IsNullOrWhiteSpace(candidateLine))
            {
                return 0;
            }

            var parts = candidateLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8)
            {
                return 0;
            }

            var protocol = parts[2].ToLowerInvariant();
            var address = parts[4];
            var component = parts[1];

            var typeIndex = Array.IndexOf(parts, "typ");
            var candidateType = typeIndex >= 0 && typeIndex + 1 < parts.Length
                ? parts[typeIndex + 1].ToLowerInvariant()
                : string.Empty;

            var score = 0;

            if (candidateType == "host" && IsPrivateAddress(address))
            {
                score += 400;
            }
            else if (candidateType == "host")
            {
                score += 320;
            }
            else if (candidateType == "srflx")
            {
                score += 200;
            }
            else if (candidateType == "prflx")
            {
                score += 150;
            }
            else if (candidateType == "relay")
            {
                score += 50;
            }

            if (protocol == "udp")
            {
                score += 40;
            }

            if (component == "1")
            {
                score += 10;
            }

            return score;
        }

        private static bool IsPrivateAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            if (IPAddress.TryParse(address, out var ip))
            {
                if (IPAddress.IsLoopback(ip))
                {
                    return true;
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var bytes = ip.GetAddressBytes();
                    return bytes[0] switch
                    {
                        10 => true,
                        172 when bytes.Length > 1 && bytes[1] >= 16 && bytes[1] <= 31 => true,
                        192 when bytes.Length > 1 && bytes[1] == 168 => true,
                        169 when bytes.Length > 1 && bytes[1] == 254 => true,
                        _ => false
                    };
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    var lower = ip.ToString().ToLowerInvariant();
                    return lower.StartsWith("fe80") || lower.StartsWith("fd") || lower.StartsWith("fc");
                }
            }
            else
            {
                var lowerAddress = address.ToLowerInvariant();
                if (lowerAddress.StartsWith("fe80") || lowerAddress.StartsWith("fd") || lowerAddress.StartsWith("fc"))
                {
                    return true;
                }
            }

            if (address.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private string EnsureCandidateHasUfrag(string? candidate, RTCPeerConnection peerConnection)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return candidate ?? string.Empty;
            }

            var candidateStr = candidate?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidateStr))
            {
                return candidateStr;
            }
            
            if (!candidateStr.StartsWith("candidate:", StringComparison.OrdinalIgnoreCase))
            {
                candidateStr = "candidate:" + candidateStr;
            }

            var candidateLower = candidateStr.ToLowerInvariant();
            if (candidateLower.Contains("ufrag"))
            {
                try
                {
                    // ✅ 重要：后端的 candidate 应该使用后端的 local description (Offer SDP) 的 ufrag
                    // 因为后端的 candidate 是发送给前端的，应该与后端的 Offer SDP 匹配
                    var localDescription = peerConnection.localDescription;
                    if (localDescription?.sdp != null)
                    {
                        var sdp = localDescription.sdp.ToString();
                        var backendUfrag = ExtractIceUfragFromSdp(sdp);
                        if (!string.IsNullOrWhiteSpace(backendUfrag))
                        {
                            var currentUfragMatch = System.Text.RegularExpressions.Regex.Match(candidateStr, @"ufrag\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (currentUfragMatch.Success)
                            {
                                var currentUfrag = currentUfragMatch.Groups[1].Value;
                                if (currentUfrag != backendUfrag)
                                {
                                    candidateStr = System.Text.RegularExpressions.Regex.Replace(candidateStr, @"ufrag\s+\w+", $"ufrag {backendUfrag}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                }
                            }
                        }
                    }
                }
                catch { }
                
                return candidateStr;
            }

            string? ufrag = null;
            try
            {
                // ✅ 重要：后端的 candidate 应该使用后端的 local description (Offer SDP) 的 ufrag
                // 而不是 remote description (Answer SDP) 的 ufrag
                // 因为后端的 candidate 是发送给前端的，应该与后端的 Offer SDP 匹配
                var localDescription = peerConnection.localDescription;
                if (localDescription?.sdp != null)
                {
                    var sdp = localDescription.sdp.ToString();
                    ufrag = ExtractIceUfragFromSdp(sdp);
                }

                // ✅ 如果 local description 没有 ufrag，才尝试从 remote description 获取（作为后备）
                if (string.IsNullOrWhiteSpace(ufrag))
                {
                    var remoteDescription = peerConnection.remoteDescription;
                    if (remoteDescription?.sdp != null)
                    {
                        var sdp = remoteDescription.sdp.ToString();
                        ufrag = ExtractIceUfragFromSdp(sdp);
                    }
                }

                if (!string.IsNullOrWhiteSpace(ufrag))
                {
                    candidateStr = candidateStr.TrimEnd();
                    if (!candidateStr.EndsWith("generation 0", StringComparison.OrdinalIgnoreCase) &&
                        !candidateStr.EndsWith("generation", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!candidateLower.Contains("generation"))
                        {
                            candidateStr += " generation 0";
                        }
                    }
                    candidateStr += " ufrag " + ufrag;
                }
                else
                {
                    _logger.LogWarning("⚠️ 无法从 SDP 中提取 ice-ufrag，candidate 将缺少 ufrag");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ 提取 ice-ufrag 失败，使用原始 candidate");
            }

            return candidateStr;
        }

        private string? ExtractIceUfragFromSdp(string sdp)
        {
            if (string.IsNullOrWhiteSpace(sdp))
            {
                return null;
            }

            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("a=ice-ufrag:", StringComparison.OrdinalIgnoreCase))
                {
                    var ufrag = line.Substring("a=ice-ufrag:".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(ufrag))
                    {
                        return ufrag;
                    }
                }
                else if (line.StartsWith("a=ice-ufrag ", StringComparison.OrdinalIgnoreCase))
                {
                    var ufrag = line.Substring("a=ice-ufrag ".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(ufrag))
                    {
                        return ufrag;
                    }
                }
            }

            return null;
        }
    }
}

