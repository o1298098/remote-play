using System.Net;
using SIPSorcery.Net;

namespace RemotePlay.Services.WebRTC
{
    /// <summary>
    /// WebRTCSignalingService SDP 处理部分
    /// </summary>
    public partial class WebRTCSignalingService
    {
        private string OptimizeSdpForLowLatency(string sdp)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sdp) || sdp.Length < 10)
                    return sdp;

                if (sdp.Contains("a=x-google-flag:low-latency") && sdp.Contains("a=minBufferedPlaybackTime"))
                    return sdp;

                var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var optimizedLines = new List<string>();
                bool foundVideo = false, foundAudio = false;
                bool videoOptimized = false, audioOptimized = false;

                foreach (var line in lines)
                {
                    optimizedLines.Add(line);
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("m=video "))
                    {
                        foundVideo = true;
                        foundAudio = false;
                        videoOptimized = false;
                    }
                    else if (trimmed.StartsWith("m=audio "))
                    {
                        foundAudio = true;
                        foundVideo = false;
                        audioOptimized = false;
                    }
                    else if (trimmed.StartsWith("m="))
                    {
                        foundAudio = false;
                        foundVideo = false;
                    }

                    if (foundVideo && !videoOptimized && trimmed.StartsWith("a=") &&
                        !trimmed.StartsWith("a=rtcp:") && trimmed.Length > 2)
                    {
                        if (!sdp.Contains("a=x-google-flag:low-latency"))
                            optimizedLines.Add("a=x-google-flag:low-latency");

                        if (!sdp.Contains("a=minBufferedPlaybackTime"))
                            optimizedLines.Add("a=minBufferedPlaybackTime:0");

                        optimizedLines.Add("a=rtcp-fb:96 nack pli");
                        optimizedLines.Add("a=rtcp-fb:96 goog-remb");
                        optimizedLines.Add("a=rtcp-fb:96 transport-cc");
                        optimizedLines.Add("a=extmap-allow-mixed");
                        optimizedLines.Add("a=fmtp:96 packetization-mode=1;max-latency=0;profile-level-id=42001f");

                        videoOptimized = true;
                    }

                    if (foundAudio && !audioOptimized && trimmed.StartsWith("a=") &&
                        !trimmed.StartsWith("a=rtcp:") && trimmed.Length > 2)
                    {
                        if (!sdp.Contains("a=minBufferedPlaybackTime"))
                            optimizedLines.Add("a=minBufferedPlaybackTime:0");

                        optimizedLines.Add("a=extmap-allow-mixed");
                        optimizedLines.Add("a=rtcp-fb:111 transport-cc");

                        audioOptimized = true;
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
                    var remoteDescription = peerConnection.remoteDescription;
                    if (remoteDescription?.sdp != null)
                    {
                        var sdp = remoteDescription.sdp.ToString();
                        var frontendUfrag = ExtractIceUfragFromSdp(sdp);
                        if (!string.IsNullOrWhiteSpace(frontendUfrag))
                        {
                            var currentUfragMatch = System.Text.RegularExpressions.Regex.Match(candidateStr, @"ufrag\s+(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (currentUfragMatch.Success)
                            {
                                var currentUfrag = currentUfragMatch.Groups[1].Value;
                                if (currentUfrag != frontendUfrag)
                                {
                                    candidateStr = System.Text.RegularExpressions.Regex.Replace(candidateStr, @"ufrag\s+\w+", $"ufrag {frontendUfrag}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
                var remoteDescription = peerConnection.remoteDescription;
                if (remoteDescription?.sdp != null)
                {
                    var sdp = remoteDescription.sdp.ToString();
                    ufrag = ExtractIceUfragFromSdp(sdp);
                }

                if (string.IsNullOrWhiteSpace(ufrag))
                {
                    var localDescription = peerConnection.localDescription;
                    if (localDescription?.sdp != null)
                    {
                        var sdp = localDescription.sdp.ToString();
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
                    _logger.LogInformation("✅ 已为 candidate 添加 ufrag: {Ufrag}", ufrag);
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

