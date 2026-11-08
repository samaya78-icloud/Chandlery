using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class UsersEnabledByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The crew migration added IsEnabled with a default of false, which switched off every account that already existed.
            // Office and supplier accounts have no crew login name and must stay signed-in-able; crew accounts are managed from the crew record.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"IsEnabled\" = TRUE WHERE \"LoginName\" IS NULL;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
