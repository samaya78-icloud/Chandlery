using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class HelpAssistantSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HelpAssistantEnabled",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HelpAssistantEnabled",
                table: "Companies");
        }
    }
}
