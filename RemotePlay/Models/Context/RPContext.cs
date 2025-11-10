using Microsoft.EntityFrameworkCore;

namespace RemotePlay.Models.Context
{
    public partial class RPContext : DbContext
    {
        public RPContext(DbContextOptions<RPContext> options)
            : base(options)
        {
        }
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
        }

        public virtual DbSet<Models.DB.PlayStation.Device> PSDevices { get; set; } = null!;
        public virtual DbSet<Models.DB.PlayStation.DeviceConfig> DeviceConfigs { get; set; } = null!;
        public virtual DbSet<Models.DB.User> Users { get; set; } = null!;
        public virtual DbSet<Models.DB.Auth.UserDevice> UserDevices { get; set; } = null!;
        public virtual DbSet<Models.DB.Base.Settings> Settings { get; set; } = null!;
        public virtual DbSet<Models.DB.Base.Log> Logs { get; set; } = null!;
        public virtual DbSet<Models.DB.Base.Enum> Enums { get; set; } = null!;
        
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            #region playstation
            modelBuilder.Entity<Models.DB.PlayStation.Device>(entity =>
            {
                entity.ToTable("t_playstation_device");
            });

            modelBuilder.Entity<Models.DB.PlayStation.DeviceConfig>(entity =>
            {
                entity.ToTable("t_device_config");
                entity.HasIndex(e => new { e.UserId, e.DeviceId, e.ConfigKey }).IsUnique();
                entity.HasOne(d => d.Device)
                    .WithMany()
                    .HasForeignKey(d => d.DeviceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
            #endregion

            #region user
            modelBuilder.Entity<Models.DB.User>(entity =>
            {
                entity.ToTable("t_user");
                entity.HasIndex(e => e.Username).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
            });

            modelBuilder.Entity<Models.DB.Auth.UserDevice>(entity =>
            {
                entity.ToTable("t_user_device");
                entity.HasIndex(e => new { e.UserId, e.DeviceId }).IsUnique();
                entity.HasOne(d => d.User)
                    .WithMany()
                    .HasForeignKey(d => d.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(d => d.Device)
                    .WithMany()
                    .HasForeignKey(d => d.DeviceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
            #endregion

            #region base
            modelBuilder.Entity<Models.DB.Base.Settings>(entity =>
            {
                entity.ToTable("t_settings");
                entity.HasIndex(e => e.Key).IsUnique();
            });

            modelBuilder.Entity<Models.DB.Base.Log>(entity =>
            {
                entity.ToTable("t_log");
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.Level, e.CreatedAt });
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.DeviceId);
            });

            modelBuilder.Entity<Models.DB.Base.Enum>(entity =>
            {
                entity.ToTable("t_enum");
                entity.HasIndex(e => new { e.EnumType, e.EnumKey }).IsUnique();
                entity.HasIndex(e => e.EnumType);
                entity.HasIndex(e => e.SortOrder);
            });
            #endregion

            OnModelCreatingPartial(modelBuilder);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
