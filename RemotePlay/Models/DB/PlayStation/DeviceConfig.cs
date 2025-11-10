using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json.Linq;

namespace RemotePlay.Models.DB.PlayStation
{
    /// <summary>
    /// 设备配置实体
    /// </summary>
    [Table("t_device_config")]
    public class DeviceConfig
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("device_id")]
        [Required]
        [MaxLength(50)]
        public required string DeviceId { get; set; }

        [Column("user_id")]
        public required string UserId { get; set; }

        [Column("config_key")]
        [Required]
        [MaxLength(100)]
        public required string ConfigKey { get; set; }

        [Column("config_value")]
        public string? ConfigValue { get; set; }

        [Column("config_json", TypeName = "jsonb")]
        public JObject? ConfigJson { get; set; }

        [Column("config_type")]
        [MaxLength(50)]
        public string? ConfigType { get; set; }

        [Column("description")]
        [MaxLength(500)]
        public string? Description { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        // 导航属性
        [ForeignKey("DeviceId")]
        public virtual Device? Device { get; set; }
        }
}
