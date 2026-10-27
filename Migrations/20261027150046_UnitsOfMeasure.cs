using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class UnitsOfMeasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowUomChange",
                table: "RfqLine",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ConversionFactor",
                table: "QuoteLine",
                type: "numeric",
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<string>(
                name: "Uom",
                table: "PoLine",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Uoms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uoms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Uoms_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UomConversions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    FromUomId = table.Column<int>(type: "integer", nullable: false),
                    ToUomId = table.Column<int>(type: "integer", nullable: false),
                    Factor = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UomConversions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UomConversions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UomConversions_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UomConversions_Uoms_FromUomId",
                        column: x => x.FromUomId,
                        principalTable: "Uoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UomConversions_Uoms_ToUomId",
                        column: x => x.ToUomId,
                        principalTable: "Uoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UomConversions_CompanyId_FromUomId_ToUomId",
                table: "UomConversions",
                columns: new[] { "CompanyId", "FromUomId", "ToUomId" },
                unique: true,
                filter: "\"ItemId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UomConversions_CompanyId_FromUomId_ToUomId_ItemId",
                table: "UomConversions",
                columns: new[] { "CompanyId", "FromUomId", "ToUomId", "ItemId" },
                unique: true,
                filter: "\"ItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UomConversions_FromUomId",
                table: "UomConversions",
                column: "FromUomId");

            migrationBuilder.CreateIndex(
                name: "IX_UomConversions_ItemId",
                table: "UomConversions",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UomConversions_ToUomId",
                table: "UomConversions",
                column: "ToUomId");

            migrationBuilder.CreateIndex(
                name: "IX_Uoms_CompanyId_Code",
                table: "Uoms",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            // Existing companies start with the units their items, requisitions and RFQs already use (named after their code);
            // the starter units and conversions are added by the seeder in development and at registration for new companies.
            migrationBuilder.Sql(@"
                INSERT INTO ""Uoms"" (""CompanyId"", ""Code"", ""Name"", ""IsActive"")
                SELECT DISTINCT ON (u.""CompanyId"", lower(u.code)) u.""CompanyId"", u.code, u.code, TRUE
                FROM (
                    SELECT i.""CompanyId"", btrim(i.""UnitOfMeasure"") AS code FROM ""Items"" i
                    UNION
                    SELECT r.""CompanyId"", btrim(l.""Uom"") FROM ""RequisitionLine"" l JOIN ""Requisitions"" r ON r.""Id"" = l.""RequisitionId""
                    UNION
                    SELECT r.""CompanyId"", btrim(l.""Uom"") FROM ""RfqLine"" l JOIN ""Rfqs"" r ON r.""Id"" = l.""RfqId""
                ) u
                WHERE u.code <> '' AND length(u.code) <= 12
                ORDER BY u.""CompanyId"", lower(u.code), u.code;");

            // Make every stored unit use the spelling of the master (Pcs and pcs become one unit).
            migrationBuilder.Sql(@"
                UPDATE ""Items"" i SET ""UnitOfMeasure"" = m.""Code"" FROM ""Uoms"" m
                WHERE m.""CompanyId"" = i.""CompanyId"" AND lower(m.""Code"") = lower(btrim(i.""UnitOfMeasure"")) AND m.""Code"" <> i.""UnitOfMeasure"";
                UPDATE ""RequisitionLine"" l SET ""Uom"" = m.""Code"" FROM ""Uoms"" m, ""Requisitions"" r
                WHERE r.""Id"" = l.""RequisitionId"" AND m.""CompanyId"" = r.""CompanyId"" AND lower(m.""Code"") = lower(btrim(l.""Uom"")) AND m.""Code"" <> l.""Uom"";
                UPDATE ""RfqLine"" l SET ""Uom"" = m.""Code"" FROM ""Uoms"" m, ""Rfqs"" r
                WHERE r.""Id"" = l.""RfqId"" AND m.""CompanyId"" = r.""CompanyId"" AND lower(m.""Code"") = lower(btrim(l.""Uom"")) AND m.""Code"" <> l.""Uom"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UomConversions");

            migrationBuilder.DropTable(
                name: "Uoms");

            migrationBuilder.DropColumn(
                name: "AllowUomChange",
                table: "RfqLine");

            migrationBuilder.DropColumn(
                name: "ConversionFactor",
                table: "QuoteLine");

            migrationBuilder.DropColumn(
                name: "Uom",
                table: "PoLine");
        }
    }
}
