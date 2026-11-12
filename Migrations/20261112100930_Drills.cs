using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class Drills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DrillTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    IntervalKind = table.Column<int>(type: "integer", nullable: false),
                    IntervalCount = table.Column<int>(type: "integer", nullable: false),
                    WarningDays = table.Column<int>(type: "integer", nullable: false),
                    EveryCrewMember = table.Column<bool>(type: "boolean", nullable: false),
                    Scenario = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Checklist = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
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
                    table.PrimaryKey("PK_DrillTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillTypes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Drills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DrillTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ShipStatus = table.Column<int>(type: "integer", nullable: false),
                    Place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scenario = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ConductedByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConductedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EquipmentTested = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Findings = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Deficiencies = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Remarks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SignedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SignedByCrewId = table.Column<Guid>(type: "uuid", nullable: true),
                    SignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("PK_Drills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drills_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Drills_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DrillOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrillTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Applies = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalKind = table.Column<int>(type: "integer", nullable: true),
                    IntervalCount = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrillOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillOverrides_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DrillOverrides_DrillTypes_DrillTypeId",
                        column: x => x.DrillTypeId,
                        principalTable: "DrillTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DrillOverrides_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrillComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrillId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_DrillComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillComments_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DrillComments_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DrillComments_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DrillNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DrillId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_DrillNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillNotes_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrillParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DrillId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Rank = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Attendance = table.Column<int>(type: "integer", nullable: false),
                    Absence = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrillParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillParticipants_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DrillComments_CompanyId",
                table: "DrillComments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillComments_DrillId_At",
                table: "DrillComments",
                columns: new[] { "DrillId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_DrillComments_VesselId",
                table: "DrillComments",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillNotes_DrillId",
                table: "DrillNotes",
                column: "DrillId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillOverrides_CompanyId",
                table: "DrillOverrides",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillOverrides_DrillTypeId",
                table: "DrillOverrides",
                column: "DrillTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillOverrides_VesselId_DrillTypeId",
                table: "DrillOverrides",
                columns: new[] { "VesselId", "DrillTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrillParticipants_CrewMemberId",
                table: "DrillParticipants",
                column: "CrewMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillParticipants_DrillId",
                table: "DrillParticipants",
                column: "DrillId");

            migrationBuilder.CreateIndex(
                name: "IX_DrillTypes_CompanyId_Name",
                table: "DrillTypes",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drills_CompanyId_Code",
                table: "Drills",
                columns: new[] { "CompanyId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_Drills_VesselId_DrillTypeId_StartedAt",
                table: "Drills",
                columns: new[] { "VesselId", "DrillTypeId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrillComments");

            migrationBuilder.DropTable(
                name: "DrillNotes");

            migrationBuilder.DropTable(
                name: "DrillOverrides");

            migrationBuilder.DropTable(
                name: "DrillParticipants");

            migrationBuilder.DropTable(
                name: "DrillTypes");

            migrationBuilder.DropTable(
                name: "Drills");
        }
    }
}
