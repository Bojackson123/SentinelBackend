using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelBackend.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase2Phase3Completion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastBootId",
                table: "DeviceConnectivityStates",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastBootId",
                table: "DeviceConnectivityStates");
        }
    }
}
