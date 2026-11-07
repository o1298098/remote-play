using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RemotePlay.DBTool.Migrations.RemotePlay
{
    /// <inheritdoc />
    public partial class AddHighBitratePresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "t_base_enum",
                columns: new[] { "id", "created_at", "description", "enum_code", "enum_key", "enum_type", "enum_value", "is_active", "sort_order", "updated_at" },
                values: new object[,]
                {
                    { "quality-ultra-20m", new DateTime(2026, 11, 7, 0, 0, 0, 0, DateTimeKind.Utc), "超高画质（约 20Mbps）", "ULTRA_20M", "ULTRA_20M", "Quality", "20000", true, 6, null },
                    { "quality-ultra-30m", new DateTime(2026, 11, 7, 0, 0, 0, 0, DateTimeKind.Utc), "超高画质（约 30Mbps）", "ULTRA_30M", "ULTRA_30M", "Quality", "30000", true, 7, null },
                    { "quality-ultra-40m", new DateTime(2026, 11, 7, 0, 0, 0, 0, DateTimeKind.Utc), "极高画质（约 40Mbps，实验性）", "ULTRA_40M", "ULTRA_40M", "Quality", "40000", true, 8, null },
                    { "quality-ultra-50m", new DateTime(2026, 11, 7, 0, 0, 0, 0, DateTimeKind.Utc), "极高画质（约 50Mbps，实验性）", "ULTRA_50M", "ULTRA_50M", "Quality", "50000", true, 9, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-20m");

            migrationBuilder.DeleteData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-30m");

            migrationBuilder.DeleteData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-40m");

            migrationBuilder.DeleteData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-50m");
        }
    }
}
