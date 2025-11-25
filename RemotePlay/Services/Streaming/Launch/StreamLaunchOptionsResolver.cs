using RemotePlay.Models.PlayStation;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemotePlay.Services.Streaming.Launch
{
    public static class StreamLaunchOptionsResolver
    {
        private const int DEFAULT_FALLBACK_BITRATE = 8000;

        private static readonly IReadOnlyDictionary<string, (int width, int height, int defaultBitrate)> ResolutionPresets
            = new Dictionary<string, (int width, int height, int defaultBitrate)>(StringComparer.OrdinalIgnoreCase)
            {
                ["360p"] = (640, 360, 2000),
                ["540p"] = (960, 540, 6000),
                ["720p"] = (1280, 720, 10000),
                ["1080p"] = (1920, 1080, 15000)
            };

        private static readonly IReadOnlyDictionary<string, int> QualityBitrates
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["very_low"] = 2000,
                ["low"] = 4000,
                ["medium"] = 6000,
                ["high"] = 10000,
                ["very_high"] = 15000,
                ["default"] = 0
            };

        private static readonly IReadOnlyDictionary<string, int> FpsPresets
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["30"] = 30,
                ["60"] = 60,
                ["low"] = 30,
                ["high"] = 60
            };

        public static StreamLaunchOptions Resolve(RemoteSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var (width, height, presetBitrate) = ResolveResolution(session.Resolution);
            int fps = ResolveFps(session.Fps);
            int bitrate = ResolveBitrate(session.Bitrate, session.Quality, presetBitrate);
            var (videoCodec, hdr) = ResolveCodec(session.StreamType);

            return new StreamLaunchOptions
            {
                Width = width,
                Height = height,
                Fps = fps,
                BitrateKbps = bitrate,
                VideoCodec = videoCodec,
                Hdr = hdr
            };
        }

        private static (int width, int height, int defaultBitrate) ResolveResolution(string? resolution)
        {
            const string fallbackKey = "720p";
            if (!string.IsNullOrWhiteSpace(resolution))
            {
                var normalized = resolution.Trim();
                if (ResolutionPresets.TryGetValue(normalized, out var preset))
                {
                    return preset;
                }

                if (TryParseResolutionDimensions(normalized, out var parsedWidth, out var parsedHeight))
                {
                    return (parsedWidth, parsedHeight, 0);
                }

                if (normalized.EndsWith("p", StringComparison.OrdinalIgnoreCase))
                {
                    var digits = normalized[..^1];
                    if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vertical) && vertical > 0)
                    {
                        if (ResolutionPresets.TryGetValue($"{vertical}p", out var derivedPreset))
                        {
                            return derivedPreset;
                        }
                        var calculatedWidth = vertical * 16 / 9;
                        return (calculatedWidth, vertical, 0);
                    }
                }
            }

            return ResolutionPresets.TryGetValue(fallbackKey, out var fallbackPreset)
                ? fallbackPreset
                : (1280, 720, 10000);
        }

        private static bool TryParseResolutionDimensions(string candidate, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            var separatorIndex = candidate.IndexOf('x');
            if (separatorIndex < 0)
            {
                separatorIndex = candidate.IndexOf('X');
            }
            if (separatorIndex <= 0 || separatorIndex >= candidate.Length - 1)
            {
                return false;
            }

            var widthPart = candidate.Substring(0, separatorIndex).Trim();
            var heightPart = candidate.Substring(separatorIndex + 1).Trim();
            if (!int.TryParse(widthPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth) ||
                !int.TryParse(heightPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight))
            {
                return false;
            }

            width = parsedWidth;
            height = parsedHeight;
            return width > 0 && height > 0;
        }

        private static int ResolveFps(string? fps)
        {
            if (!string.IsNullOrWhiteSpace(fps))
            {
                var normalized = fps.Trim();
                if (FpsPresets.TryGetValue(normalized, out var presetFps))
                {
                    return presetFps;
                }

                if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFps) && parsedFps > 0)
                {
                    return parsedFps;
                }
            }

            return 60;
        }

        private static int ResolveBitrate(string? bitrate, string? quality, int presetBitrate)
        {
            if (!string.IsNullOrWhiteSpace(bitrate) &&
                int.TryParse(bitrate.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitBitrate) &&
                explicitBitrate > 0)
            {
                return explicitBitrate;
            }

            if (!string.IsNullOrWhiteSpace(quality) &&
                QualityBitrates.TryGetValue(quality.Trim(), out var qualityBitrate) &&
                qualityBitrate > 0)
            {
                return qualityBitrate;
            }

            if (presetBitrate > 0)
            {
                return presetBitrate;
            }

            return DEFAULT_FALLBACK_BITRATE;
        }

        private static (string videoCodec, bool hdr) ResolveCodec(string? streamType)
        {
            const string defaultCodec = "hevc";
            bool hdr = false;

            if (string.IsNullOrWhiteSpace(streamType))
            {
                return (defaultCodec, hdr);
            }

            var normalized = streamType.Trim();
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var streamTypeCode))
            {
                return streamTypeCode switch
                {
                    1 => ("avc", false),
                    2 => ("hevc", false),
                    3 => ("hevc", true),
                    _ => (defaultCodec, hdr)
                };
            }

            var upper = normalized.ToUpperInvariant();
            return upper switch
            {
                "H264" => ("avc", false),
                "AVC" => ("avc", false),
                "HEVC_HDR" => ("hevc", true),
                "HDR" => ("hevc", true),
                "HEVC" => ("hevc", false),
                _ => (defaultCodec, hdr)
            };
        }
    }
}

