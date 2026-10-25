using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class Phase2RfqQuotationUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LeadTimeDays is dropped at the end, after being carried into Quotes.DeliveryDays.
            migrationBuilder.RenameColumn(
                name: "Remarks",
                table: "QuoteLine",
                newName: "OfferText");

            migrationBuilder.AddColumn<string>(
                name: "NotificationEmails",
                table: "Suppliers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BuyerDesignation",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BuyerEmail",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BuyerName",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "DateRequired",
                table: "Rfqs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryPortId",
                table: "Rfqs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "Rfqs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EquipmentDescription",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EquipmentMaker",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EquipmentParticulars",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EquipmentSerialNo",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Rfqs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VesselEta",
                table: "Rfqs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "RfqLine",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DrawingNo",
                table: "RfqLine",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Uom",
                table: "RfqLine",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AdditionalChargeDescription",
                table: "Quotes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "AdditionalCharges",
                table: "Quotes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Quotes",
                type: "text",
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryDays",
                table: "Quotes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPct",
                table: "Quotes",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "Quotes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SupplierRef",
                table: "Quotes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidityDate",
                table: "Quotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPct",
                table: "QuoteLine",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Uom",
                table: "QuoteLine",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalChargeDescription",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "AdditionalCharges",
                table: "PurchaseOrders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPct",
                table: "PurchaseOrders",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountPct",
                table: "PoLine",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "RfqArchives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RfqId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqArchives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RfqArchives_Rfqs_RfqId",
                        column: x => x.RfqId,
                        principalTable: "Rfqs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RfqArchives_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rfqs_DeliveryPortId",
                table: "Rfqs",
                column: "DeliveryPortId");

            migrationBuilder.CreateIndex(
                name: "IX_RfqArchives_RfqId_SupplierId",
                table: "RfqArchives",
                columns: new[] { "RfqId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RfqArchives_SupplierId",
                table: "RfqArchives",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_Rfqs_Ports_DeliveryPortId",
                table: "Rfqs",
                column: "DeliveryPortId",
                principalTable: "Ports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill existing rows so current data stays meaningful.
            migrationBuilder.Sql(@"UPDATE ""RfqLine"" l SET ""Uom"" = i.""UnitOfMeasure"" FROM ""Items"" i WHERE i.""Id"" = l.""ItemId"" AND l.""Uom"" = '';");
            migrationBuilder.Sql(@"UPDATE ""Quotes"" q SET ""DeliveryDays"" = s.d FROM (SELECT ""QuoteId"", MAX(""LeadTimeDays"") AS d FROM ""QuoteLine"" GROUP BY ""QuoteId"") s WHERE s.""QuoteId"" = q.""Id"" AND s.d > 0;");

            migrationBuilder.DropColumn(
                name: "LeadTimeDays",
                table: "QuoteLine");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rfqs_Ports_DeliveryPortId",
                table: "Rfqs");

            migrationBuilder.DropTable(
                name: "RfqArchives");

            migrationBuilder.DropIndex(
                name: "IX_Rfqs_DeliveryPortId",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "NotificationEmails",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "BuyerDesignation",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "BuyerEmail",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "BuyerName",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "DateRequired",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "DeliveryPortId",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EquipmentDescription",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EquipmentMaker",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EquipmentParticulars",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EquipmentSerialNo",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "VesselEta",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "RfqLine");

            migrationBuilder.DropColumn(
                name: "DrawingNo",
                table: "RfqLine");

            migrationBuilder.DropColumn(
                name: "Uom",
                table: "RfqLine");

            migrationBuilder.DropColumn(
                name: "AdditionalChargeDescription",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "AdditionalCharges",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "DeliveryDays",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "DiscountPct",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "SupplierRef",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ValidityDate",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "DiscountPct",
                table: "QuoteLine");

            migrationBuilder.DropColumn(
                name: "Uom",
                table: "QuoteLine");

            migrationBuilder.DropColumn(
                name: "AdditionalChargeDescription",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "AdditionalCharges",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "DiscountPct",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "DiscountPct",
                table: "PoLine");

            migrationBuilder.RenameColumn(
                name: "OfferText",
                table: "QuoteLine",
                newName: "Remarks");

            migrationBuilder.AddColumn<int>(
                name: "LeadTimeDays",
                table: "QuoteLine",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
