using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemotePlay.DBTool.Migrations.RemotePlay
{
    /// <inheritdoc />
    public partial class UpdateBitratePresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-default",
                column: "description",
                value: "自动（按分辨率匹配码率）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-high",
                column: "description",
                value: "10 Mbps · 高清");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-low",
                column: "description",
                value: "4 Mbps · 流畅");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-medium",
                column: "description",
                value: "6 Mbps · 标准");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-20m",
                column: "description",
                value: "20 Mbps · 高画质");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-30m",
                column: "description",
                value: "30 Mbps · 超高画质");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-40m",
                column: "description",
                value: "40 Mbps · 极高画质，需稳定的局域网");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-50m",
                column: "description",
                value: "50 Mbps · 极致画质，建议有线网络");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-very-high",
                column: "description",
                value: "15 Mbps · 1080p 推荐");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-very-low",
                column: "description",
                value: "2 Mbps · 省流量，适合移动网络");

            migrationBuilder.InsertData(
                table: "t_base_enum",
                columns: new[] { "id", "created_at", "description", "enum_code", "enum_key", "enum_type", "enum_value", "is_active", "sort_order", "updated_at" },
                values: new object[] { "quality-ultra-100m", new DateTime(2026, 11, 9, 0, 0, 0, 0, DateTimeKind.Utc), "100 Mbps · 最高画质，需千兆有线网络", "ULTRA_100M", "ULTRA_100M", "Quality", "100000", true, 10, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-100m");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-default",
                column: "description",
                value: "自动匹配比特率");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-high",
                column: "description",
                value: "高画质（约 10Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-low",
                column: "description",
                value: "低画质（约 4Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-medium",
                column: "description",
                value: "中等画质（约 6Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-20m",
                column: "description",
                value: "超高画质（约 20Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-30m",
                column: "description",
                value: "超高画质（约 30Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-40m",
                column: "description",
                value: "极高画质（约 40Mbps，实验性）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-ultra-50m",
                column: "description",
                value: "极高画质（约 50Mbps，实验性）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-very-high",
                column: "description",
                value: "非常高画质（约 15Mbps）");

            migrationBuilder.UpdateData(
                table: "t_base_enum",
                keyColumn: "id",
                keyValue: "quality-very-low",
                column: "description",
                value: "非常低画质（约 2Mbps）");
        }
    }
}
