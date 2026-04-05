using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelBackend.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LatestDeviceStates",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PanelVoltage = table.Column<double>(type: "float", nullable: true),
                    PumpCurrent = table.Column<double>(type: "float", nullable: true),
                    HighWaterAlarm = table.Column<bool>(type: "bit", nullable: true),
                    TemperatureC = table.Column<double>(type: "float", nullable: true),
                    SignalRssi = table.Column<int>(type: "int", nullable: true),
                    RuntimeSeconds = table.Column<int>(type: "int", nullable: true),
                    CycleCount = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LatestDeviceStates", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LatestStateDeviceId = table.Column<string>(type: "nvarchar(128)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_LatestDeviceStates_LatestStateDeviceId",
                        column: x => x.LatestStateDeviceId,
                        principalTable: "LatestDeviceStates",
                        principalColumn: "DeviceId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceId",
                table: "Devices",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_LatestStateDeviceId",
                table: "Devices",
                column: "LatestStateDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "LatestDeviceStates");
        }
    }
}
