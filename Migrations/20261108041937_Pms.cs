using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class Pms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineryClasses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineryClasses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineryClasses_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineryClasses_MachineryClasses_ParentId",
                        column: x => x.ParentId,
                        principalTable: "MachineryClasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineryClassId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CalendarInterval = table.Column<int>(type: "integer", nullable: true),
                    CalendarUnit = table.Column<int>(type: "integer", nullable: false),
                    HoursInterval = table.Column<int>(type: "integer", nullable: true),
                    Anchor = table.Column<int>(type: "integer", nullable: false),
                    GraceDays = table.Column<int>(type: "integer", nullable: true),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "integer", nullable: true),
                    RequiresVerification = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTemplates_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobTemplates_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobTemplates_MachineryClasses_MachineryClassId",
                        column: x => x.MachineryClassId,
                        principalTable: "MachineryClasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MachineryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Maker = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Serial = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    MachineryClassId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    HasHourMeter = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_MachineryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MachineryItems_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineryItems_MachineryClasses_MachineryClassId",
                        column: x => x.MachineryClassId,
                        principalTable: "MachineryClasses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MachineryItems_MachineryItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "MachineryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MachineryItems_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlannedJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: true),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    CalendarInterval = table.Column<int>(type: "integer", nullable: true),
                    CalendarUnit = table.Column<int>(type: "integer", nullable: false),
                    HoursInterval = table.Column<int>(type: "integer", nullable: true),
                    Anchor = table.Column<int>(type: "integer", nullable: false),
                    FixedAnchorDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GraceDays = table.Column<int>(type: "integer", nullable: true),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "integer", nullable: true),
                    RequiresVerification = table.Column<bool>(type: "boolean", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BaselineDoneDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BaselineDoneHours = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    LastDoneDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDoneHours = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: true),
                    NextDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueBasis = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    HoursEstimated = table.Column<bool>(type: "boolean", nullable: false),
                    HoursUnknown = table.Column<bool>(type: "boolean", nullable: false),
                    LastReadingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    GraceUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedJobs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlannedJobs_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedJobs_JobTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "JobTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedJobs_MachineryItems_MachineryItemId",
                        column: x => x.MachineryItemId,
                        principalTable: "MachineryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlannedJobs_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunningHoursReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Hours = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: false),
                    Source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    IsCorrection = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunningHoursReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunningHoursReadings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RunningHoursReadings_MachineryItems_MachineryItemId",
                        column: x => x.MachineryItemId,
                        principalTable: "MachineryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunningHoursReadings_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobCompletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    DoneDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HoursAtDone = table.Column<decimal>(type: "numeric(12,1)", precision: 12, scale: 1, nullable: true),
                    PerformedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TimeSpentMinutes = table.Column<int>(type: "integer", nullable: true),
                    Comments = table.Column<string>(type: "text", nullable: false),
                    IsImported = table.Column<bool>(type: "boolean", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerStage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "office"),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobCompletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobCompletions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobCompletions_PlannedJobs_PlannedJobId",
                        column: x => x.PlannedJobId,
                        principalTable: "PlannedJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobCompletions_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerifiedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("PK_JobVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobVerifications_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobVerifications_JobCompletions_CompletionId",
                        column: x => x.CompletionId,
                        principalTable: "JobCompletions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobVerifications_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobCompletions_CompanyId",
                table: "JobCompletions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobCompletions_PlannedJobId_DoneDate",
                table: "JobCompletions",
                columns: new[] { "PlannedJobId", "DoneDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JobCompletions_VesselId",
                table: "JobCompletions",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_CompanyId_Code",
                table: "JobTemplates",
                columns: new[] { "CompanyId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_DepartmentId",
                table: "JobTemplates",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplates_MachineryClassId",
                table: "JobTemplates",
                column: "MachineryClassId");

            migrationBuilder.CreateIndex(
                name: "IX_JobVerifications_CompanyId",
                table: "JobVerifications",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobVerifications_CompletionId",
                table: "JobVerifications",
                column: "CompletionId");

            migrationBuilder.CreateIndex(
                name: "IX_JobVerifications_VesselId",
                table: "JobVerifications",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryClasses_CompanyId",
                table: "MachineryClasses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryClasses_ParentId",
                table: "MachineryClasses",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryItems_CompanyId",
                table: "MachineryItems",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryItems_MachineryClassId",
                table: "MachineryItems",
                column: "MachineryClassId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryItems_ParentId",
                table: "MachineryItems",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineryItems_VesselId_ParentId",
                table: "MachineryItems",
                columns: new[] { "VesselId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedJobs_CompanyId",
                table: "PlannedJobs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedJobs_DepartmentId",
                table: "PlannedJobs",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedJobs_MachineryItemId",
                table: "PlannedJobs",
                column: "MachineryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedJobs_TemplateId",
                table: "PlannedJobs",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedJobs_VesselId_NextDueDate",
                table: "PlannedJobs",
                columns: new[] { "VesselId", "NextDueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RunningHoursReadings_CompanyId",
                table: "RunningHoursReadings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RunningHoursReadings_MachineryItemId_ReadingDate",
                table: "RunningHoursReadings",
                columns: new[] { "MachineryItemId", "ReadingDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RunningHoursReadings_VesselId",
                table: "RunningHoursReadings",
                column: "VesselId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobVerifications");

            migrationBuilder.DropTable(
                name: "RunningHoursReadings");

            migrationBuilder.DropTable(
                name: "JobCompletions");

            migrationBuilder.DropTable(
                name: "PlannedJobs");

            migrationBuilder.DropTable(
                name: "JobTemplates");

            migrationBuilder.DropTable(
                name: "MachineryItems");

            migrationBuilder.DropTable(
                name: "MachineryClasses");
        }
    }
}
