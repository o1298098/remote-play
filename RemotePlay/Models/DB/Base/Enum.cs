using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RemotePlay.Models.DB.Base
{
    /// <summary>
    /// 系统枚举字典实体
    /// </summary>
    [Table("t_enum")]
    public class Enum
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("enum_type")]
        [Required]
        [MaxLength(100)]
        public required string EnumType { get; set; }

        [Column("enum_key")]
        [Required]
        [MaxLength(100)]
        public required string EnumKey { get; set; }

        [Column("enum_value")]
        [Required]
        [MaxLength(200)]
        public required string EnumValue { get; set; }

        [Column("enum_code")]
        [MaxLength(50)]
        public string? EnumCode { get; set; }

        [Column("sort_order")]
        public int SortOrder { get; set; } = 0;

        [Column("is_active")]
        public bool IsActive { get; set; } = true;

        [Column("description")]
        [MaxLength(500)]
        public string? Description { get; set; }

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }
}
