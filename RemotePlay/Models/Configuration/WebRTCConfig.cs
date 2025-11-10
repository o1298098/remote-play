using System.Collections.Generic;

namespace RemotePlay.Models.Configuration
{
    public class WebRTCConfig
    {
        /// <summary>
        /// 对外可达的主机 IP，用于在容器/NAT 环境下覆盖本地候选地址。
        /// </summary>
        public string? PublicIp { get; set; }

        /// <summary>
        /// 指定 WebRTC 使用的 UDP 端口起始值（包含）。必须为偶数。
        /// </summary>
        public int? IcePortMin { get; set; }

        /// <summary>
        /// 指定 WebRTC 使用的 UDP 端口结束值（包含）。必须为偶数且大于等于起始值。
        /// </summary>
        public int? IcePortMax { get; set; }

        /// <summary>
        /// 是否对端口进行伪随机分配。默认开启以减少端口冲突。
        /// </summary>
        public bool ShufflePorts { get; set; } = true;

        /// <summary>
        /// TURN 服务列表，可选。
        /// </summary>
        public List<TurnServerConfig> TurnServers { get; set; } = new();

        /// <summary>
        /// 是否在生成 SDP 时优先保留局域网候选地址，默认开启以优化局域网低延迟。
        /// </summary>
        public bool PreferLanCandidates { get; set; } = true;
    }

    public class TurnServerConfig
    {
        public string? Url { get; set; }
        public string? Username { get; set; }
        public string? Credential { get; set; }
    }
}


