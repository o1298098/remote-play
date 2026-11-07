using Google.Protobuf;
using RemotePlay.Protos;
using System.Text.Unicode;

namespace RemotePlay.Services.Streaming.Protocol
{
    public static class ProtoCodec
    {
        public static byte[] BuildBigPayload(int clientVersion, byte[] sessionKey, byte[] launchSpec, byte[] encryptedKey, byte[]? ecdhPub = null, byte[]? ecdhSig = null)
        {
            // 🔹 sessionKey 应该是原始二进制数据转 ASCII 字符串（或 base64），不是 UTF8
            // 约定：sessionKey 在构建时已经是可打印字符
            string sessionKeyStr = System.Text.Encoding.ASCII.GetString(sessionKey);
            string launchSpecStr = System.Text.Encoding.ASCII.GetString(launchSpec);
            
            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Big,
                BigPayload = new BigPayload
                {
                    ClientVersion = (uint)clientVersion,
                    SessionKey = sessionKeyStr,
                    LaunchSpec = launchSpecStr,
                    EncryptedKey = Google.Protobuf.ByteString.CopyFrom(encryptedKey ?? Array.Empty<byte>())
                }
            };
            
            if (ecdhPub != null) msg.BigPayload.EcdhPubKey = Google.Protobuf.ByteString.CopyFrom(ecdhPub);
            if (ecdhSig != null) msg.BigPayload.EcdhSig = Google.Protobuf.ByteString.CopyFrom(ecdhSig);
            
            return msg.ToByteArray();
        }

        public static bool TryParse(byte[] data, out TakionMessage msg)
        {
            msg = null!;
            try
            {
                msg = TakionMessage.Parser.ParseFrom(data);
                return true;
            }
            catch { return false; }
        }

        public static byte[] BuildStreamInfoAck()
        {
            var msg = new TakionMessage { Type = TakionMessage.Types.PayloadType.Streaminfoack };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建 ControllerConnection 消息
        /// </summary>
        public static byte[] BuildControllerConnection(int controllerId = 0, bool isPs5 = true)
        {
            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Controllerconnection,
                ControllerConnectionPayload = new ControllerConnectionPayload
                {
                    Connected = true,
                    // ✅ PS5 使用 DUALSENSE (6)，PS4 使用 DUALSHOCK4 (2)
                    ControllerType = isPs5 
                        ? ControllerConnectionPayload.Types.ControllerType.Dualsense 
                        : ControllerConnectionPayload.Types.ControllerType.Dualshock4
                }
            };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建 MicConnection 消息（麦克风连接）
        /// 这会在 PS 主机上显示麦克风连接通知
        /// </summary>
        public static byte[] BuildMicConnection(int controllerId = 0, bool connected = true)
        {
            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Micconnection,
                MicConnectionPayload = new MicConnectionPayload
                {
                    ControllerId = controllerId,
                    Connected = connected,
                    Result = true
                }
            };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建麦克风启用消息（STREAMINFO with audio header）
        /// 这是关键消息：在这之后主机会开始接收音视频流
        /// </summary>
        public static byte[] BuildMicrophoneEnable()
        {
            // 构建音频头：16位，1声道，48000Hz，480 samples per frame
            // 参考既有实现：audio_header_set(&audio_header_input, 16, 1, 48000, 480)
            var audioHeader = new byte[16];
            audioHeader[0] = 0; audioHeader[1] = 1;  // channels = 1 (big-endian uint16)
            audioHeader[2] = 0; audioHeader[3] = 0; audioHeader[4] = 0xBB; audioHeader[5] = 0x80; // sample_rate = 48000
            audioHeader[6] = 0; audioHeader[7] = 16; // bits_per_sample = 16
            audioHeader[8] = 0x01; audioHeader[9] = 0xE0; // frame_size = 480
            // 其余字节保持为 0

            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Streaminfo,
                StreamInfoPayload = new StreamInfoPayload
                {
                    AudioHeader = Google.Protobuf.ByteString.CopyFrom(audioHeader)
                }
            };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建心跳消息
        /// </summary>
        public static byte[] BuildHeartbeat()
        {
            var msg = new TakionMessage { Type = TakionMessage.Types.PayloadType.Heartbeat };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建 ClientInfo（携带 session_key）。部分固件需要该消息后才开始推流。
        /// </summary>
        public static byte[] BuildClientInfo(string sessionKey, uint? gcmTag = null, uint? keyPos = null)
        {
            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Clientinfo,
                ClientInfoPayload = new ClientInfoPayload
                {
                    SessionKey = sessionKey
                }
            };
            if (gcmTag.HasValue) msg.ClientInfoPayload.GcmTag = gcmTag.Value;
            if (keyPos.HasValue) msg.ClientInfoPayload.KeyPos = keyPos.Value;
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建 IDR 请求（请求关键帧）。
        /// </summary>
        public static byte[] BuildIdrRequest()
        {
            var msg = new TakionMessage { Type = TakionMessage.Types.PayloadType.Idrrequest };
            return msg.ToByteArray();
        }

        /// <summary>
        /// 构建损坏帧通知，主机据此让后续帧改为参考 start 之前的完好帧。
        /// </summary>
        public static byte[] BuildCorruptFrame(uint start, uint end)
        {
            var msg = new TakionMessage
            {
                Type = TakionMessage.Types.PayloadType.Corruptframe,
                CorruptPayload = new CorruptFramePayload { Start = start, End = end }
            };
            return msg.ToByteArray();
        }
    }
}



