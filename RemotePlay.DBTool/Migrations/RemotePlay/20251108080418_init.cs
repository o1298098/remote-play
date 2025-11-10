using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Newtonsoft.Json.Linq;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RemotePlay.DBTool.Migrations.RemotePlay
{
    /// <inheritdoc />
    public partial class init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "t_enum",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    enum_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    enum_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    enum_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enum_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_enum", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "t_log",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    exception = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    device_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    request_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    properties = table.Column<JObject>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "t_playstation_device",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<string>(type: "text", nullable: false),
                    host_name = table.Column<string>(type: "text", nullable: false),
                    host_type = table.Column<string>(type: "text", nullable: true),
                    mac_address = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    system_version = table.Column<string>(type: "text", nullable: true),
                    discover_protocol_version = table.Column<string>(type: "text", nullable: true),
                    ap_bssid = table.Column<string>(type: "text", nullable: true),
                    is_registered = table.Column<bool>(type: "boolean", nullable: true),
                    rp_key = table.Column<string>(type: "text", nullable: true),
                    rp_key_type = table.Column<string>(type: "text", nullable: true),
                    regist_key = table.Column<string>(type: "text", nullable: true),
                    regist_data = table.Column<JObject>(type: "jsonb", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    last_play_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_playstation_device", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "t_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    value_json = table.Column<JObject>(type: "jsonb", nullable: true),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_encrypted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "t_user",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    email = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    avatar_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "t_device_config",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    device_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    config_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    config_value = table.Column<string>(type: "text", nullable: true),
                    config_json = table.Column<JObject>(type: "jsonb", nullable: true),
                    config_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_device_config", x => x.id);
                    table.ForeignKey(
                        name: "FK_t_device_config_t_playstation_device_device_id",
                        column: x => x.device_id,
                        principalTable: "t_playstation_device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "t_user_device",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    user_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    device_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    device_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_t_user_device", x => x.id);
                    table.ForeignKey(
                        name: "FK_t_user_device_t_playstation_device_device_id",
                        column: x => x.device_id,
                        principalTable: "t_playstation_device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_t_user_device_t_user_user_id",
                        column: x => x.user_id,
                        principalTable: "t_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "t_enum",
                columns: new[] { "id", "created_at", "description", "enum_code", "enum_key", "enum_type", "enum_value", "is_active", "sort_order", "updated_at" },
                values: new object[,]
                {
                    { "fps-high", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "60 帧每秒", "60FPS", "HIGH", "FPS", "60", true, 2, null },
                    { "fps-low", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "30 帧每秒", "30FPS", "LOW", "FPS", "30", true, 1, null },
                    { "quality-default", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "自动匹配比特率", "DEFAULT", "DEFAULT", "Quality", "0", true, 0, null },
                    { "quality-high", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "高画质（约 10Mbps）", "HIGH", "HIGH", "Quality", "10000", true, 4, null },
                    { "quality-low", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "低画质（约 4Mbps）", "LOW", "LOW", "Quality", "4000", true, 2, null },
                    { "quality-medium", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "中等画质（约 6Mbps）", "MEDIUM", "MEDIUM", "Quality", "6000", true, 3, null },
                    { "quality-very-high", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "非常高画质（约 15Mbps）", "VERY_HIGH", "VERY_HIGH", "Quality", "15000", true, 5, null },
                    { "quality-very-low", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "非常低画质（约 2Mbps）", "VERY_LOW", "VERY_LOW", "Quality", "2000", true, 1, null },
                    { "resolution-1080p", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "1080p 分辨率预设", "RES_1080P", "1080p", "ResolutionPreset", "{\"width\":1920,\"height\":1080,\"bitrate\":15000}", true, 4, null },
                    { "resolution-360p", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "360p 分辨率预设", "RES_360P", "360p", "ResolutionPreset", "{\"width\":640,\"height\":360,\"bitrate\":2000}", true, 1, null },
                    { "resolution-540p", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "540p 分辨率预设", "RES_540P", "540p", "ResolutionPreset", "{\"width\":960,\"height\":540,\"bitrate\":6000}", true, 2, null },
                    { "resolution-720p", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "720p 分辨率预设", "RES_720P", "720p", "ResolutionPreset", "{\"width\":1280,\"height\":720,\"bitrate\":10000}", true, 3, null },
                    { "streamtype-h264", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "H.264 视频流", "H264", "H264", "StreamType", "1", true, 1, null },
                    { "streamtype-hevc", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "HEVC/H.265 视频流", "HEVC", "HEVC", "StreamType", "2", true, 2, null },
                    { "streamtype-hevc-hdr", new DateTime(2025, 11, 1, 0, 0, 0, 0, DateTimeKind.Utc), "HEVC/H.265 HDR 视频流", "HEVC_HDR", "HEVC_HDR", "StreamType", "3", true, 3, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_t_device_config_device_id_config_key",
                table: "t_device_config",
                columns: new[] { "device_id", "config_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_enum_enum_type",
                table: "t_enum",
                column: "enum_type");

            migrationBuilder.CreateIndex(
                name: "IX_t_enum_enum_type_enum_key",
                table: "t_enum",
                columns: new[] { "enum_type", "enum_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_enum_sort_order",
                table: "t_enum",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "IX_t_log_created_at",
                table: "t_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_t_log_device_id",
                table: "t_log",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_t_log_level_created_at",
                table: "t_log",
                columns: new[] { "level", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_t_log_user_id",
                table: "t_log",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_t_settings_key",
                table: "t_settings",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_user_email",
                table: "t_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_user_username",
                table: "t_user",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_t_user_device_device_id",
                table: "t_user_device",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_t_user_device_user_id_device_id",
                table: "t_user_device",
                columns: new[] { "user_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "t_device_config");

            migrationBuilder.DropTable(
                name: "t_enum");

            migrationBuilder.DropTable(
                name: "t_log");

            migrationBuilder.DropTable(
                name: "t_settings");

            migrationBuilder.DropTable(
                name: "t_user_device");

            migrationBuilder.DropTable(
                name: "t_playstation_device");

            migrationBuilder.DropTable(
                name: "t_user");
        }
    }
}
