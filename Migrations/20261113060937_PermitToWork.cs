using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class PermitToWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PermitTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    MaxValidityHours = table.Column<int>(type: "integer", nullable: false),
                    RequesterRole = table.Column<int>(type: "integer", nullable: false),
                    SameRequesterAndIssuer = table.Column<bool>(type: "boolean", nullable: false),
                    GasTestRequired = table.Column<bool>(type: "boolean", nullable: false),
                    GasRepeatMinutes = table.Column<int>(type: "integer", nullable: false),
                    OxygenMin = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    OxygenMax = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    FlammableMaxPercentLel = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    H2sMaxPpm = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    CoMaxPpm = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    AttendantRequired = table.Column<bool>(type: "boolean", nullable: false),
                    EntryLogRequired = table.Column<bool>(type: "boolean", nullable: false),
                    RiskAssessmentRequired = table.Column<bool>(type: "boolean", nullable: false),
                    MasterApprovalRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ChecklistVersion = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitTypes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Permits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PermitTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ChecklistVersion = table.Column<int>(type: "integer", nullable: false),
                    Terms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TankId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Isolation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Conditions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RequesterCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequesterName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IssuerCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssuerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AttendantCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttendantName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RiskAssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorklistTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlannedJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Permits_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Permits_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Section = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    NoteRequired = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitChecklistItems_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermitChecklistItems_PermitTypes_PermitTypeId",
                        column: x => x.PermitTypeId,
                        principalTable: "PermitTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermitConflictPairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitTypeAId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitTypeBId = table.Column<Guid>(type: "uuid", nullable: false),
                    SameLocationOnly = table.Column<bool>(type: "boolean", nullable: false),
                    Message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitConflictPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitConflictPairs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermitConflictPairs_PermitTypes_PermitTypeAId",
                        column: x => x.PermitTypeAId,
                        principalTable: "PermitTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermitConflictPairs_PermitTypes_PermitTypeBId",
                        column: x => x.PermitTypeBId,
                        principalTable: "PermitTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitAnswers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChecklistItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Section = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    NoteRequired = table.Column<bool>(type: "boolean", nullable: false),
                    Answer = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitAnswers_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermitComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
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
                    table.PrimaryKey("PK_PermitComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitComments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermitComments_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PermitComments_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PermitEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    PersonName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    In = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Out = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttendantName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitEntries_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermitEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitEvents_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermitGasReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CalibrationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Oxygen = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    FlammablePercentLel = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    H2sPpm = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    CoPpm = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    WithinLimits = table.Column<bool>(type: "boolean", nullable: false),
                    Failures = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitGasReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitGasReadings_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermitPersons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PermitId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermitPersons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermitPersons_Permits_PermitId",
                        column: x => x.PermitId,
                        principalTable: "Permits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermitAnswers_PermitId",
                table: "PermitAnswers",
                column: "PermitId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitChecklistItems_CompanyId",
                table: "PermitChecklistItems",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitChecklistItems_PermitTypeId_Version_Position",
                table: "PermitChecklistItems",
                columns: new[] { "PermitTypeId", "Version", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_PermitComments_CompanyId",
                table: "PermitComments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitComments_PermitId_At",
                table: "PermitComments",
                columns: new[] { "PermitId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PermitComments_VesselId",
                table: "PermitComments",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitConflictPairs_CompanyId",
                table: "PermitConflictPairs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitConflictPairs_PermitTypeAId",
                table: "PermitConflictPairs",
                column: "PermitTypeAId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitConflictPairs_PermitTypeBId",
                table: "PermitConflictPairs",
                column: "PermitTypeBId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitEntries_PermitId",
                table: "PermitEntries",
                column: "PermitId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitEvents_PermitId_At",
                table: "PermitEvents",
                columns: new[] { "PermitId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PermitGasReadings_PermitId",
                table: "PermitGasReadings",
                column: "PermitId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitPersons_PermitId",
                table: "PermitPersons",
                column: "PermitId");

            migrationBuilder.CreateIndex(
                name: "IX_PermitTypes_CompanyId_Name",
                table: "PermitTypes",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permits_CompanyId_Code",
                table: "Permits",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Permits_VesselId_ValidFrom",
                table: "Permits",
                columns: new[] { "VesselId", "ValidFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermitAnswers");

            migrationBuilder.DropTable(
                name: "PermitChecklistItems");

            migrationBuilder.DropTable(
                name: "PermitComments");

            migrationBuilder.DropTable(
                name: "PermitConflictPairs");

            migrationBuilder.DropTable(
                name: "PermitEntries");

            migrationBuilder.DropTable(
                name: "PermitEvents");

            migrationBuilder.DropTable(
                name: "PermitGasReadings");

            migrationBuilder.DropTable(
                name: "PermitPersons");

            migrationBuilder.DropTable(
                name: "PermitTypes");

            migrationBuilder.DropTable(
                name: "Permits");
        }
    }
}
