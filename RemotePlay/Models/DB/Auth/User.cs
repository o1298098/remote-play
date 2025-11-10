using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RemotePlay.Models.DB
{
    /// <summary>
    /// 用户实体，用于认证和授权
    /// </summary>
    [Table("t_user")]
    public class User
    {
        [Key]
        [Column("id")]
        [MaxLength(50)]
        public required string Id { get; set; }

        [Column("username")]
        [Required]
        [MaxLength(50)]
        public required string Username { get; set; }

        [Column("email")]
        [Required]
        [MaxLength(100)]
        [EmailAddress]
        public required string Email { get; set; }

        [Column("avatar_url")]
        [MaxLength(500)]
        public string? AvatarUrl { get; set; }

        [Column("password_hash")]
        [Required]
        [MaxLength(256)]
        public required string PasswordHash { get; set; }

        [Column("created_at")]
        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [Column("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        [Column("last_login_at")]
        public DateTime? LastLoginAt { get; set; }

        [Column("is_active")]
        public bool IsActive { get; set; } = true;
    }
}

