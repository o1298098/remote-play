using System.Buffers;
using System.Text;
using System.Text.Json;

namespace RemotePlay.Services.Streaming.Protocol
{
    public class LaunchSpec
    {
        public string SessionId { get; set; } = "sessionId4321";
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint MaxFps { get; set; }
        public uint BwKbpsSent { get; set; }
        public uint Mtu { get; set; }
        public uint Rtt { get; set; }
        public byte[] HandshakeKey { get; set; } = Array.Empty<byte>();
        public string Target { get; set; } = "ps5"; // ps5 / ps4 / ps3
        public string Codec { get; set; } = "avc"; // avc / hevc
        public bool Hdr { get; set; } = false;
    }
    public static class LaunchSpecFormatter
    {
        public static string Format(LaunchSpec spec)
        {
            string handshakeKeyB64 = Convert.ToBase64String(spec.HandshakeKey);

            var buffer = new ArrayBufferWriter<byte>();
            var writerOptions = new JsonWriterOptions { Indented = false };

            using (var jsonWriter = new Utf8JsonWriter(buffer, writerOptions))
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WriteString("sessionId", spec.SessionId);

                jsonWriter.WriteStartArray("streamResolutions");
                jsonWriter.WriteStartObject();
                jsonWriter.WriteStartObject("resolution");
                jsonWriter.WriteNumber("width", spec.Width);
                jsonWriter.WriteNumber("height", spec.Height);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteNumber("maxFps", spec.MaxFps);
                jsonWriter.WriteNumber("score", 10);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndArray();

                jsonWriter.WriteStartObject("network");
                jsonWriter.WriteNumber("bwKbpsSent", spec.BwKbpsSent);
                jsonWriter.WriteNumber("bwLoss", 0.001);
                jsonWriter.WriteNumber("mtu", spec.Mtu);
                jsonWriter.WriteNumber("rtt", spec.Rtt);
                jsonWriter.WriteStartArray("ports");
                jsonWriter.WriteNumberValue(53);
                jsonWriter.WriteNumberValue(2053);
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();

                jsonWriter.WriteNumber("slotId", 1);

                jsonWriter.WriteStartObject("appSpecification");
                jsonWriter.WriteNumber("minFps", 30);
                jsonWriter.WriteNumber("minBandwidth", 0);
                jsonWriter.WriteString("extTitleId", "ps3");
                jsonWriter.WriteNumber("version", 1);
                jsonWriter.WriteNumber("timeLimit", 1);
                jsonWriter.WriteNumber("startTimeout", 100);
                jsonWriter.WriteNumber("afkTimeout", 100);
                jsonWriter.WriteNumber("afkTimeoutDisconnect", 100);
                jsonWriter.WriteEndObject();

                jsonWriter.WriteStartObject("konan");
                jsonWriter.WriteString("ps3AccessToken", "accessToken");
                jsonWriter.WriteString("ps3RefreshToken", "refreshToken");
                jsonWriter.WriteEndObject();

                jsonWriter.WriteStartObject("requestGameSpecification");
                jsonWriter.WriteString("model", "bravia_tv");
                jsonWriter.WriteString("platform", "android");
                jsonWriter.WriteString("audioChannels", "5.1");
                jsonWriter.WriteString("language", "sp");
                jsonWriter.WriteString("acceptButton", "X");
                jsonWriter.WriteStartArray("connectedControllers");
                jsonWriter.WriteStringValue("xinput");
                jsonWriter.WriteStringValue("ds3");
                jsonWriter.WriteStringValue("ds4");
                jsonWriter.WriteEndArray();
                jsonWriter.WriteString("yuvCoefficient", "bt601");
                jsonWriter.WriteString("videoEncoderProfile", "hw4.1");
                jsonWriter.WriteString("audioEncoderProfile", "audio1");

                if (spec.Target is "ps5" or "ps4")
                {
                    jsonWriter.WriteString("adaptiveStreamMode", "resize");
                    jsonWriter.WriteString("videoCodec", spec.Codec == "hevc" ? "hevc" : "avc");
                    jsonWriter.WriteString("dynamicRange", spec.Hdr ? "HDR" : "SDR");
                }

                jsonWriter.WriteEndObject(); // requestGameSpecification

                jsonWriter.WriteStartObject("userProfile");
                jsonWriter.WriteString("onlineId", "psnId");
                jsonWriter.WriteString("npId", "npId");
                jsonWriter.WriteString("region", "US");
                jsonWriter.WriteStartArray("languagesUsed");
                jsonWriter.WriteStringValue("en");
                jsonWriter.WriteStringValue("jp");
                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();

                jsonWriter.WriteString("handshakeKey", handshakeKeyB64);

                jsonWriter.WriteEndObject(); // root

                jsonWriter.Flush();
            }

            // 从 buffer 读取已写入的数据
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
