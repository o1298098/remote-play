using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json.Linq;

namespace RemotePlay.Models.DB.Base
{
    /// <summary>
    /// 日志记录实体
    /// </summary>
    [Table("t_log")]
    public class Log
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("level")]
        [Required]
        [MaxLength(20)]
        public required string Level { get; set; }

        [Column("message")]
        [Required]
        public required string Message { get; set; }

        [Column("exception")]
        public string? Exception { get; set; }

        [Column("source")]
        [MaxLength(200)]
        public string? Source { get; set; }

        [Column("category")]
        [MaxLength(100)]
        public string? Category { get; set; }

        [Column("user_id")]
        [MaxLength(50)]
        public string? UserId { get; set; }

        [Column("device_id")]
        [MaxLength(50)]
        public string? DeviceId { get; set; }

        [Column("ip_address")]
        [MaxLength(50)]
        public string? IpAddress { get; set; }

        [Column("request_path")]
        [MaxLength(500)]
        public string? RequestPath { get; set; }

        [Column("properties", TypeName = "jsonb")]
        public JObject? Properties { get; set; }

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
