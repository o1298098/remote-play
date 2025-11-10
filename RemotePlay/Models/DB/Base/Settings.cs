using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Newtonsoft.Json.Linq;

namespace RemotePlay.Models.DB.Base
{
    /// <summary>
    /// 系统设置实体
    /// </summary>
    public class Settings
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("key")]
        [Required]
        [MaxLength(100)]
        public required string Key { get; set; }

        [Column("value")]
        public string? Value { get; set; }

        [Column("value_json", TypeName = "jsonb")]
        public JObject? ValueJson { get; set; }

        [Column("category")]
        [MaxLength(50)]
        public string? Category { get; set; }

        [Column("description")]
        [MaxLength(500)]
        public string? Description { get; set; }

        [Column("is_encrypted")]
        public bool IsEncrypted { get; set; } = false;

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }
}
