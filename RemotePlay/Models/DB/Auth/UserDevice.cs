using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RemotePlay.Models.DB.Auth
{
    /// <summary>
    /// 用户设备关联实体
    /// </summary>
    public class UserDevice
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("user_id")]
        [Required]
        [MaxLength(50)]
        public required string UserId { get; set; }

        [Column("device_id")]
        [Required]
        [MaxLength(50)]
        public required string DeviceId { get; set; }

        [Column("device_name")]
        [MaxLength(200)]
        public string? DeviceName { get; set; }

        [Column("device_type")]
        [MaxLength(50)]
        public string? DeviceType { get; set; }

        [Column("is_default")]
        public bool IsDefault { get; set; } = false;

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("last_used_at")]
        public DateTime? LastUsedAt { get; set; }

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        // 导航属性
        [ForeignKey("UserId")]
        public virtual Models.DB.User? User { get; set; }

        [ForeignKey("DeviceId")]
        public virtual Models.DB.PlayStation.Device? Device { get; set; }
    }
}
