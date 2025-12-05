using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RemotePlay.Models.DB.PlayStation
{
    public class Device
    {
        [Key]
        [Column("id")]
        public required string Id { get; set; }

        [Column("uuid")]
        public required Guid uuid { get; set; }

        [Column("host_id")]
        public required string HostId { get; set; }

        [Column("host_name")]
        public required string HostName { get; set; }

        [Column("host_type")]
        public string? HostType { get; set; }

        [Column("mac_address")]
        public string? MacAddress { get; set; }

        [Column("ip_address")]
        public string? IpAddress { get; set; }

        [Column("system_version")]
        public string? SystemVersion { get; set; }

        [Column("discover_protocol_version")]
        public string? DiscoverProtocolVersion { get; set; }

        [Column("ap_bssid")]
        public string? APBssid { get; set; }

        [Column("is_registered")]
        public bool? IsRegistered { get; set; }
        [Column("rp_key")]
        public string? RPKey { get; set; }
        [Column("rp_key_type")]
        public string? RPKeyType { get; set; }
        [Column("regist_key")]
        public string? RegistKey { get; set; }
        [Column("regist_data", TypeName = "jsonb")]
        public JObject? RegistData { get; set; }
        [Column("notes")]
        public string? Notes { get; set; }
        [Column("last_play_date")]
        public DateTime? LastPlayDate { get; set; }
        [Column("last_seen_at")]
        public DateTime? LastSeenAt { get; set; }
        [Column("status")]
        public string? Status { get; set; }

    }
}
