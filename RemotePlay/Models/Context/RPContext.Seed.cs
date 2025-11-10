using System;
using Microsoft.EntityFrameworkCore;
using EnumEntity = RemotePlay.Models.DB.Base.Enum;

namespace RemotePlay.Models.Context
{
    public partial class RPContext
    {
        private static readonly DateTime SeedCreatedAt = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EnumEntity>().HasData(
                // StreamType
                new EnumEntity
                {
                    Id = "streamtype-h264",
                    EnumType = "StreamType",
                    EnumKey = "H264",
                    EnumValue = "1",
                    EnumCode = "H264",
                    SortOrder = 1,
                    IsActive = true,
                    Description = "H.264 视频流",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "streamtype-hevc",
                    EnumType = "StreamType",
                    EnumKey = "HEVC",
                    EnumValue = "2",
                    EnumCode = "HEVC",
                    SortOrder = 2,
                    IsActive = true,
                    Description = "HEVC/H.265 视频流",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "streamtype-hevc-hdr",
                    EnumType = "StreamType",
                    EnumKey = "HEVC_HDR",
                    EnumValue = "3",
                    EnumCode = "HEVC_HDR",
                    SortOrder = 3,
                    IsActive = true,
                    Description = "HEVC/H.265 HDR 视频流",
                    CreatedAt = SeedCreatedAt
                },

                // Quality
                new EnumEntity
                {
                    Id = "quality-default",
                    EnumType = "Quality",
                    EnumKey = "DEFAULT",
                    EnumValue = "0",
                    EnumCode = "DEFAULT",
                    SortOrder = 0,
                    IsActive = true,
                    Description = "自动匹配比特率",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "quality-very-low",
                    EnumType = "Quality",
                    EnumKey = "VERY_LOW",
                    EnumValue = "2000",
                    EnumCode = "VERY_LOW",
                    SortOrder = 1,
                    IsActive = true,
                    Description = "非常低画质（约 2Mbps）",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "quality-low",
                    EnumType = "Quality",
                    EnumKey = "LOW",
                    EnumValue = "4000",
                    EnumCode = "LOW",
                    SortOrder = 2,
                    IsActive = true,
                    Description = "低画质（约 4Mbps）",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "quality-medium",
                    EnumType = "Quality",
                    EnumKey = "MEDIUM",
                    EnumValue = "6000",
                    EnumCode = "MEDIUM",
                    SortOrder = 3,
                    IsActive = true,
                    Description = "中等画质（约 6Mbps）",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "quality-high",
                    EnumType = "Quality",
                    EnumKey = "HIGH",
                    EnumValue = "10000",
                    EnumCode = "HIGH",
                    SortOrder = 4,
                    IsActive = true,
                    Description = "高画质（约 10Mbps）",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "quality-very-high",
                    EnumType = "Quality",
                    EnumKey = "VERY_HIGH",
                    EnumValue = "15000",
                    EnumCode = "VERY_HIGH",
                    SortOrder = 5,
                    IsActive = true,
                    Description = "非常高画质（约 15Mbps）",
                    CreatedAt = SeedCreatedAt
                },

                // FPS
                new EnumEntity
                {
                    Id = "fps-low",
                    EnumType = "FPS",
                    EnumKey = "LOW",
                    EnumValue = "30",
                    EnumCode = "30FPS",
                    SortOrder = 1,
                    IsActive = true,
                    Description = "30 帧每秒",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "fps-high",
                    EnumType = "FPS",
                    EnumKey = "HIGH",
                    EnumValue = "60",
                    EnumCode = "60FPS",
                    SortOrder = 2,
                    IsActive = true,
                    Description = "60 帧每秒",
                    CreatedAt = SeedCreatedAt
                },

                // Resolution Presets
                new EnumEntity
                {
                    Id = "resolution-360p",
                    EnumType = "ResolutionPreset",
                    EnumKey = "360p",
                    EnumValue = "{\"width\":640,\"height\":360,\"bitrate\":2000}",
                    EnumCode = "RES_360P",
                    SortOrder = 1,
                    IsActive = true,
                    Description = "360p 分辨率预设",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "resolution-540p",
                    EnumType = "ResolutionPreset",
                    EnumKey = "540p",
                    EnumValue = "{\"width\":960,\"height\":540,\"bitrate\":6000}",
                    EnumCode = "RES_540P",
                    SortOrder = 2,
                    IsActive = true,
                    Description = "540p 分辨率预设",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "resolution-720p",
                    EnumType = "ResolutionPreset",
                    EnumKey = "720p",
                    EnumValue = "{\"width\":1280,\"height\":720,\"bitrate\":10000}",
                    EnumCode = "RES_720P",
                    SortOrder = 3,
                    IsActive = true,
                    Description = "720p 分辨率预设",
                    CreatedAt = SeedCreatedAt
                },
                new EnumEntity
                {
                    Id = "resolution-1080p",
                    EnumType = "ResolutionPreset",
                    EnumKey = "1080p",
                    EnumValue = "{\"width\":1920,\"height\":1080,\"bitrate\":15000}",
                    EnumCode = "RES_1080P",
                    SortOrder = 4,
                    IsActive = true,
                    Description = "1080p 分辨率预设",
                    CreatedAt = SeedCreatedAt
                }
            );
        }
    }
}

