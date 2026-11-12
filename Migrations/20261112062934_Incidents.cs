using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class Incidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncidentCauses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Group = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentCauses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentCauses_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentSettingsRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    LateReportHours = table.Column<int>(type: "integer", nullable: false),
                    RepeatWindowMonths = table.Column<int>(type: "integer", nullable: false),
                    InvestigationDays = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    UrgentForLostTime = table.Column<bool>(type: "boolean", nullable: false),
                    UrgentForPollution = table.Column<bool>(type: "boolean", nullable: false),
                    AllowAnonymousToOffice = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentSettingsRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentSettingsRows_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ShipStatus = table.Column<int>(type: "integer", nullable: false),
                    Weather = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ImmediateAction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EquipmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EquipmentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RiskAssessmentRef = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ReporterCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReporterName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AnonymousToOffice = table.Column<bool>(type: "boolean", nullable: false),
                    ShareLesson = table.Column<bool>(type: "boolean", nullable: false),
                    ActualConsequence = table.Column<int>(type: "integer", nullable: false),
                    PotentialConsequence = table.Column<int>(type: "integer", nullable: false),
                    Likelihood = table.Column<int>(type: "integer", nullable: false),
                    Band = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsUrgent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Incidents_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentEvents_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentEvents_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentInvestigations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Facts = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ImmediateCause = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RootCauses = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    WhyChain = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContributingFactors = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AssessmentFinding = table.Column<int>(type: "integer", nullable: false),
                    Lessons = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    InvestigatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Finished = table.Column<bool>(type: "boolean", nullable: false),
                    FinishedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentInvestigations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentInvestigations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentInvestigations_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentInvestigations_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentPersons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Rank = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Company = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    InjuryType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BodyPart = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Treatment = table.Column<int>(type: "integer", nullable: false),
                    DaysLost = table.Column<int>(type: "integer", nullable: false),
                    RestrictedDays = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentPersons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentPersons_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentSupplements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentSupplements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentSupplements_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentCauses_CompanyId",
                table: "IncidentCauses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_CompanyId",
                table: "IncidentEvents",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_IncidentId_At",
                table: "IncidentEvents",
                columns: new[] { "IncidentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_VesselId",
                table: "IncidentEvents",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentInvestigations_CompanyId",
                table: "IncidentInvestigations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentInvestigations_IncidentId_Version",
                table: "IncidentInvestigations",
                columns: new[] { "IncidentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentInvestigations_VesselId",
                table: "IncidentInvestigations",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentPersons_IncidentId",
                table: "IncidentPersons",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentSettingsRows_CompanyId",
                table: "IncidentSettingsRows",
                column: "CompanyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentSupplements_IncidentId",
                table: "IncidentSupplements",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CompanyId_Code",
                table: "Incidents",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_VesselId_OccurredAt",
                table: "Incidents",
                columns: new[] { "VesselId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentCauses");

            migrationBuilder.DropTable(
                name: "IncidentEvents");

            migrationBuilder.DropTable(
                name: "IncidentInvestigations");

            migrationBuilder.DropTable(
                name: "IncidentPersons");

            migrationBuilder.DropTable(
                name: "IncidentSettingsRows");

            migrationBuilder.DropTable(
                name: "IncidentSupplements");

            migrationBuilder.DropTable(
                name: "Incidents");
        }
    }
}
