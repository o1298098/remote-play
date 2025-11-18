using Newtonsoft.Json;
using RemotePlay.Utils.Crypto;
using System;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RemotePlay.Services.Streaming
{
    public static class ProtoHandler
    {
        public static byte[] BuildLaunchSpec(
            byte[] sessionId,
            string hostType,
            byte[] handshakeKey,
            int width,
            int height,
            int fps,
            int bitrateKbps,
            string videoCodec,
            bool hdr,
            int rtt,
            int mtu
        )
        {
            // 使用 Utf8JsonWriter 生成最小 JSON，确保顺序与数值格式可控
            bool isPs5 = string.Equals(hostType, "PS5", StringComparison.OrdinalIgnoreCase);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();

                writer.WriteString("sessionId", "sessionId4321");

                writer.WritePropertyName("streamResolutions");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("resolution");
                writer.WriteStartObject();
                writer.WriteNumber("width", width);
                writer.WriteNumber("height", height);
                writer.WriteEndObject();
                writer.WriteNumber("maxFps", fps);
                writer.WriteNumber("score", 10);
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WritePropertyName("network");
                writer.WriteStartObject();
                writer.WriteNumber("bwKbpsSent", bitrateKbps);
                // 精确写出 0.001000（6 位小数）
                writer.WritePropertyName("bwLoss");
                writer.WriteRawValue("0.001000");
                writer.WriteNumber("mtu", mtu);
                writer.WriteNumber("rtt", rtt);
                writer.WritePropertyName("ports");
                writer.WriteStartArray();
                writer.WriteNumberValue(53);
                writer.WriteNumberValue(2053);
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteNumber("slotId", 1);

                writer.WritePropertyName("appSpecification");
                writer.WriteStartObject();
                writer.WriteNumber("minFps", 30);
                writer.WriteNumber("minBandwidth", 0);
                writer.WriteString("extTitleId", "ps3");
                writer.WriteNumber("version", 1);
                writer.WriteNumber("timeLimit", 1);
                writer.WriteNumber("startTimeout", 100);
                writer.WriteNumber("afkTimeout", 100);
                writer.WriteNumber("afkTimeoutDisconnect", 100);
                writer.WriteEndObject();

                writer.WritePropertyName("konan");
                writer.WriteStartObject();
                writer.WriteString("ps3AccessToken", "accessToken");
                writer.WriteString("ps3RefreshToken", "refreshToken");
                writer.WriteEndObject();

                writer.WritePropertyName("requestGameSpecification");
                writer.WriteStartObject();
                writer.WriteString("model", "bravia_tv");
                writer.WriteString("platform", "android");
                writer.WriteString("audioChannels", "5.1");
                writer.WriteString("language", "sp");
                writer.WriteString("acceptButton", "X");
                writer.WritePropertyName("connectedControllers");
                writer.WriteStartArray();
                writer.WriteStringValue("xinput");
                writer.WriteStringValue("ds3");
                writer.WriteStringValue("ds4");
                writer.WriteEndArray();
                writer.WriteString("yuvCoefficient", "bt601");
                writer.WriteString("videoEncoderProfile", "hw4.1");
                writer.WriteString("audioEncoderProfile", "audio1"); 
                if (isPs5)
                {
                    writer.WriteString("adaptiveStreamMode", "resize");
                }
                writer.WriteEndObject();

                writer.WritePropertyName("userProfile");
                writer.WriteStartObject();
                writer.WriteString("onlineId", "psnId");
                writer.WriteString("npId", "npId");
                writer.WriteString("region", "US");
                writer.WritePropertyName("languagesUsed");
                writer.WriteStartArray();
                writer.WriteStringValue("en");
                writer.WriteStringValue("jp");
                writer.WriteEndArray();
                writer.WriteEndObject();

                // ✅ videoCodec 和 dynamicRange 应该在顶级对象中
                writer.WriteString("videoCodec", videoCodec == "hevc" ? "hevc" : "avc");
                writer.WriteString("dynamicRange", hdr ? "HDR" : "SDR");
                writer.WriteString("handshakeKey", Convert.ToBase64String(handshakeKey));

                writer.WriteEndObject();
                writer.Flush();
            }

            var jsonBytes = buffer.WrittenSpan.ToArray();
            // 结尾追加单个 0x00 字节
            return jsonBytes.Concat(new byte[] { 0x00 }).ToArray();
        }

        public static byte[] EncodeLaunchSpecWithSession(string hostType, byte[] key, byte[] nonce, byte[] plain)
        {
            var sess = new SessionCipher(hostType, key, nonce, 0);
            var zeros = new byte[plain.Length];
            var keystream = sess.Encrypt(zeros, counter: 0);

            var xor = new byte[plain.Length];
            for (int i = 0; i < plain.Length; i++)
                xor[i] = (byte)(keystream[i] ^ plain[i]);

            var b64 = Convert.ToBase64String(xor);
            return Encoding.ASCII.GetBytes(b64);
        }

        public static byte[] BigPayload(byte[] encodedLaunchSpec)
        {
            // 发送的是纯 base64 数据，不包含前缀 tag
            return encodedLaunchSpec;
        }

        public static byte[] DisconnectPayload() =>
            Encoding.ASCII.GetBytes("DISCONNECT");

        /// <summary>
        /// 构建 FeedbackState 包（控制器状态）- 默认空闲状态
        /// Python: FeedbackState.pack() + FeedbackPacket.bytes()
        /// </summary>
        public static byte[] FeedbackState(string hostType)
        {
            bool isPs5 = string.Equals(hostType, "PS5", StringComparison.OrdinalIgnoreCase);
            var emptyState = new ControllerState();
            return emptyState.Pack(isPs5);
        }

        /// <summary>
        /// 构建 FeedbackState 包（控制器状态）- 带自定义状态
        /// </summary>
        public static byte[] FeedbackState(string hostType, ControllerState state)
        {
            bool isPs5 = string.Equals(hostType, "PS5", StringComparison.OrdinalIgnoreCase);
            return state.Pack(isPs5);
        }

        public static byte[] Feedback(int type, int sequence, byte[] data)
        {
            string tag = $"FEEDBACK|t={type}|seq={sequence}|d={Convert.ToBase64String(data ?? Array.Empty<byte>())}";
            return Encoding.ASCII.GetBytes(tag);
        }

        /// <summary>
        /// 构建 Congestion 数据（网络拥塞统计）
        /// 3 字节的简单格式
        /// </summary>
        public static byte[] Congestion(int received, int lost)
        {
            // Congestion 包 payload 只有 3 字节
            // 简化为最小格式：[0x00, 0x00, 0x01]
            return new byte[] { 0x00, 0x00, 0x01 };
        }

        public static byte[] CorruptFrame(int start, int end)
        {
            string tag = $"CORRUPT|s={start}|e={end}";
            return Encoding.ASCII.GetBytes(tag);
        }
    }
}
