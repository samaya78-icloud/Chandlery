using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RestWorkTableSign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkArrangementSignOffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false),
                    SignedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SignedByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkArrangementSignOffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkArrangementSignOffs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkArrangementSignOffs_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementSignOffs_CompanyId",
                table: "WorkArrangementSignOffs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementSignOffs_VesselId_VersionNo",
                table: "WorkArrangementSignOffs",
                columns: new[] { "VesselId", "VersionNo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkArrangementSignOffs");
        }
    }
}
