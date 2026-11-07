using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class ProcurementOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerStage",
                table: "PurchaseOrders",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "office");

            migrationBuilder.AddColumn<string>(
                name: "OwnerStage",
                table: "DeliveryOrders",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "office");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerStage",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "OwnerStage",
                table: "DeliveryOrders");
        }
    }
}
