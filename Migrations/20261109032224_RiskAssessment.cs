using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RiskAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RaCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaCategories_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RaMatrixBands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MinFactor = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaMatrixBands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaMatrixBands_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RaMatrixLabels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Axis = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaMatrixLabels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaMatrixLabels_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RaTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Contingency = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaTemplates_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    PreviousVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CategoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    JobDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AssessedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Contingency = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ReviewPeriodMonths = table.Column<int>(type: "integer", nullable: false),
                    SubmissionCount = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HighestResidualFactor = table.Column<int>(type: "integer", nullable: false),
                    HighestResidualBand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
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
                    table.PrimaryKey("PK_RiskAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskAssessments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RiskAssessments_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaTemplateActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaTemplateActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaTemplateActivities_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RaTemplateActivities_RaTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "RaTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaTemplateHazards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hazard = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Effect = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExistingControls = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AdditionalControls = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ConsequenceBefore = table.Column<int>(type: "integer", nullable: false),
                    ProbabilityBefore = table.Column<int>(type: "integer", nullable: false),
                    ConsequenceAfter = table.Column<int>(type: "integer", nullable: false),
                    ProbabilityAfter = table.Column<int>(type: "integer", nullable: false),
                    Contingency = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaTemplateHazards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaTemplateHazards_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RaTemplateHazards_RaTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "RaTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaActivities_RiskAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "RiskAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RaHazards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hazard = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Effect = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ConsequenceBefore = table.Column<int>(type: "integer", nullable: false),
                    ProbabilityBefore = table.Column<int>(type: "integer", nullable: false),
                    ExistingControls = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AdditionalControls = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ConsequenceAfter = table.Column<int>(type: "integer", nullable: false),
                    ProbabilityAfter = table.Column<int>(type: "integer", nullable: false),
                    ResidualReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ResponsibleName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Contingency = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    FactorBefore = table.Column<int>(type: "integer", nullable: false),
                    BandBefore = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FactorAfter = table.Column<int>(type: "integer", nullable: false),
                    BandAfter = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorklistTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaHazards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaHazards_RiskAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "RiskAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RaSignOffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ForSubmission = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ReviewerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ReviewerRank = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
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
                    table.PrimaryKey("PK_RaSignOffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaSignOffs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RaSignOffs_RiskAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "RiskAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RaSignOffs_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RaTeamMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Rank = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaTeamMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RaTeamMembers_RiskAssessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "RiskAssessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RaActivities_AssessmentId",
                table: "RaActivities",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RaCategories_CompanyId",
                table: "RaCategories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaHazards_AssessmentId",
                table: "RaHazards",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RaMatrixBands_CompanyId",
                table: "RaMatrixBands",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaMatrixLabels_CompanyId",
                table: "RaMatrixLabels",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaSignOffs_AssessmentId",
                table: "RaSignOffs",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RaSignOffs_CompanyId",
                table: "RaSignOffs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaSignOffs_VesselId",
                table: "RaSignOffs",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTeamMembers_AssessmentId",
                table: "RaTeamMembers",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTemplateActivities_CompanyId",
                table: "RaTemplateActivities",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTemplateActivities_TemplateId",
                table: "RaTemplateActivities",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTemplateHazards_CompanyId",
                table: "RaTemplateHazards",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTemplateHazards_TemplateId",
                table: "RaTemplateHazards",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_RaTemplates_CompanyId",
                table: "RaTemplates",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_CompanyId_Code_Version",
                table: "RiskAssessments",
                columns: new[] { "CompanyId", "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_VesselId_CreatedAt",
                table: "RiskAssessments",
                columns: new[] { "VesselId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RaActivities");

            migrationBuilder.DropTable(
                name: "RaCategories");

            migrationBuilder.DropTable(
                name: "RaHazards");

            migrationBuilder.DropTable(
                name: "RaMatrixBands");

            migrationBuilder.DropTable(
                name: "RaMatrixLabels");

            migrationBuilder.DropTable(
                name: "RaSignOffs");

            migrationBuilder.DropTable(
                name: "RaTeamMembers");

            migrationBuilder.DropTable(
                name: "RaTemplateActivities");

            migrationBuilder.DropTable(
                name: "RaTemplateHazards");

            migrationBuilder.DropTable(
                name: "RiskAssessments");

            migrationBuilder.DropTable(
                name: "RaTemplates");
        }
    }
}
