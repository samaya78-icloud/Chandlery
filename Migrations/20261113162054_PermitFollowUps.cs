using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class PermitFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RescueDrillTypeId",
                table: "PermitTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PermitJobRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitJobRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitJobRules_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermitJobRules_PermitTypes_PermitTypeId",
                        column: x => x.PermitTypeId,
                        principalTable: "PermitTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermitJobRules_CompanyId_JobTemplateId",
                table: "PermitJobRules",
                columns: new[] { "CompanyId", "JobTemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermitJobRules_PermitTypeId",
                table: "PermitJobRules",
                column: "PermitTypeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermitJobRules");

            migrationBuilder.DropColumn(
                name: "RescueDrillTypeId",
                table: "PermitTypes");
        }
    }
}
