using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SentinelBackend.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddedFocalPointNameToCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FocalPointName",
                table: "Companies",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FocalPointName",
                table: "Companies");
        }
    }
}
