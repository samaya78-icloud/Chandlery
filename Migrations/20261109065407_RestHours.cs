using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RestHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestBreachNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Rule = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExceptionKind = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorklistTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestBreachNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestBreachNotes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RestBreachNotes_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RestEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Rank = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorkSpans = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EnteredByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EnteredByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    OnBehalf = table.Column<bool>(type: "boolean", nullable: false),
                    EnteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestEntries_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RestEntries_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RestRuleSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    FlagState = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MinRest24Minutes = table.Column<int>(type: "integer", nullable: false),
                    MinRest7Minutes = table.Column<int>(type: "integer", nullable: false),
                    MaxRestPeriods = table.Column<int>(type: "integer", nullable: false),
                    MinLongestPeriodMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    ReductionAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    ReductionMinConsecutiveMinutes = table.Column<int>(type: "integer", nullable: false),
                    ReductionMaxDays = table.Column<int>(type: "integer", nullable: false),
                    ReductionMin7Minutes = table.Column<int>(type: "integer", nullable: false),
                    MinorNightProtection = table.Column<bool>(type: "boolean", nullable: false),
                    MinorNightStartMinute = table.Column<int>(type: "integer", nullable: false),
                    MinorNightLengthMinutes = table.Column<int>(type: "integer", nullable: false),
                    SignIntervalDays = table.Column<int>(type: "integer", nullable: false),
                    RetentionMonths = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestRuleSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestRuleSets_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RestRuleSets_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RestSignOffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FromDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("PK_RestSignOffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestSignOffs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RestSignOffs_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VesselDayStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PortName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SetByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "vessel"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VesselDayStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VesselDayStatuses_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselDayStatuses_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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

            migrationBuilder.CreateTable(
                name: "WorkArrangementTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LawReference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    LimitsText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OtherRequirements = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("PK_WorkArrangementTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkArrangementTables_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkArrangementTables_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestBreachNotes_CompanyId",
                table: "RestBreachNotes",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RestBreachNotes_CrewMemberId_Date",
                table: "RestBreachNotes",
                columns: new[] { "CrewMemberId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_RestBreachNotes_VesselId",
                table: "RestBreachNotes",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_RestEntries_CompanyId",
                table: "RestEntries",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RestEntries_CrewMemberId_Date_EnteredAt",
                table: "RestEntries",
                columns: new[] { "CrewMemberId", "Date", "EnteredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RestEntries_VesselId_Date",
                table: "RestEntries",
                columns: new[] { "VesselId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_RestRuleSets_CompanyId_VesselId",
                table: "RestRuleSets",
                columns: new[] { "CompanyId", "VesselId" });

            migrationBuilder.CreateIndex(
                name: "IX_RestRuleSets_VesselId",
                table: "RestRuleSets",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_RestSignOffs_CompanyId",
                table: "RestSignOffs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RestSignOffs_CrewMemberId_FromDate",
                table: "RestSignOffs",
                columns: new[] { "CrewMemberId", "FromDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RestSignOffs_VesselId",
                table: "RestSignOffs",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselDayStatuses_CompanyId",
                table: "VesselDayStatuses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselDayStatuses_VesselId_Date_At",
                table: "VesselDayStatuses",
                columns: new[] { "VesselId", "Date", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementSignOffs_CompanyId",
                table: "WorkArrangementSignOffs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementSignOffs_VesselId_VersionNo",
                table: "WorkArrangementSignOffs",
                columns: new[] { "VesselId", "VersionNo" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementTables_CompanyId",
                table: "WorkArrangementTables",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkArrangementTables_VesselId_VersionNo",
                table: "WorkArrangementTables",
                columns: new[] { "VesselId", "VersionNo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestBreachNotes");

            migrationBuilder.DropTable(
                name: "RestEntries");

            migrationBuilder.DropTable(
                name: "RestRuleSets");

            migrationBuilder.DropTable(
                name: "RestSignOffs");

            migrationBuilder.DropTable(
                name: "VesselDayStatuses");

            migrationBuilder.DropTable(
                name: "WorkArrangementSignOffs");

            migrationBuilder.DropTable(
                name: "WorkArrangementTables");
        }
    }
}
