using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class TankInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TankChecklistTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TankType = table.Column<int>(type: "integer", nullable: false),
                    ShipType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankChecklistTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankChecklistTemplates_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tanks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    PlanX = table.Column<int>(type: "integer", nullable: false),
                    PlanY = table.Column<int>(type: "integer", nullable: false),
                    PlanWidth = table.Column<int>(type: "integer", nullable: false),
                    PlanHeight = table.Column<int>(type: "integer", nullable: false),
                    SteelGradeDeck = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SteelGradeBottom = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SteelGradeSide = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SteelGradeBulkhead = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OriginalCoating = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CoatedAreaM2 = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: true),
                    CathodicProtection = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProtectedAreaM2 = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tanks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tanks_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tanks_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TankCriteria",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scale = table.Column<int>(type: "integer", nullable: false),
                    ExceptionValues = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankCriteria_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TankCriteria_TankChecklistTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "TankChecklistTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TankInspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    TankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    InspectedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Place = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    WorkDone = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlannedJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    GeneralComments = table.Column<string>(type: "text", nullable: false),
                    InspectorName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubmissionCount = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExceptionCount = table.Column<int>(type: "integer", nullable: false),
                    HasBadRating = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankInspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankInspections_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TankInspections_Tanks_TankId",
                        column: x => x.TankId,
                        principalTable: "Tanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankInspections_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TankInspectionResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriterionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Group = table.Column<int>(type: "integer", nullable: false),
                    CriterionName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scale = table.Column<int>(type: "integer", nullable: false),
                    ExceptionValues = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    WorklistTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankInspectionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankInspectionResults_TankInspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "TankInspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TankInspectionReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ForSubmission = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ReviewerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankInspectionReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankInspectionReviews_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TankInspectionReviews_TankInspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "TankInspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankInspectionReviews_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TankChecklistTemplates_CompanyId",
                table: "TankChecklistTemplates",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TankCriteria_CompanyId",
                table: "TankCriteria",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TankCriteria_TemplateId",
                table: "TankCriteria",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_TankInspectionResults_InspectionId",
                table: "TankInspectionResults",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_TankInspectionReviews_CompanyId",
                table: "TankInspectionReviews",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TankInspectionReviews_InspectionId",
                table: "TankInspectionReviews",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_TankInspectionReviews_VesselId",
                table: "TankInspectionReviews",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_TankInspections_CompanyId_Code",
                table: "TankInspections",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankInspections_TankId_InspectedOn",
                table: "TankInspections",
                columns: new[] { "TankId", "InspectedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TankInspections_VesselId",
                table: "TankInspections",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_Tanks_CompanyId",
                table: "Tanks",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Tanks_VesselId_Code",
                table: "Tanks",
                columns: new[] { "VesselId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TankCriteria");

            migrationBuilder.DropTable(
                name: "TankInspectionResults");

            migrationBuilder.DropTable(
                name: "TankInspectionReviews");

            migrationBuilder.DropTable(
                name: "TankChecklistTemplates");

            migrationBuilder.DropTable(
                name: "TankInspections");

            migrationBuilder.DropTable(
                name: "Tanks");
        }
    }
}
