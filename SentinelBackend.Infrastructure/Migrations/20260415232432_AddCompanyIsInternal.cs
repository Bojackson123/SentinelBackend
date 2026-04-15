using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelBackend.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyIsInternal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInternal",
                table: "Companies",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsInternal",
                table: "Companies");
        }
    }
}
