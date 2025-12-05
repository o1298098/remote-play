using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemotePlay.DBTool.Migrations.RemotePlay
{
    /// <inheritdoc />
    public partial class AddLastSeenAtToDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_seen_at",
                table: "t_playstation_device",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "t_playstation_device");
        }
    }
}
