using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class PmsDeferrals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeferredUntil",
                table: "PlannedJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobDeferralRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastDoneAtRequest = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
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
                    table.PrimaryKey("PK_JobDeferralRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobDeferralRequests_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobDeferralRequests_PlannedJobs_PlannedJobId",
                        column: x => x.PlannedJobId,
                        principalTable: "PlannedJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobDeferralRequests_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PlannedJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Proposed = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
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
                    table.PrimaryKey("PK_PlanChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanChangeRequests_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanChangeRequests_MachineryItems_MachineryItemId",
                        column: x => x.MachineryItemId,
                        principalTable: "MachineryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanChangeRequests_PlannedJobs_PlannedJobId",
                        column: x => x.PlannedJobId,
                        principalTable: "PlannedJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanChangeRequests_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobDeferralDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovedDueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DecidedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DecidedByRole = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_JobDeferralDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobDeferralDecisions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobDeferralDecisions_JobDeferralRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "JobDeferralRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobDeferralDecisions_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanChangeDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DecidedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
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
                    table.PrimaryKey("PK_PlanChangeDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanChangeDecisions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanChangeDecisions_PlanChangeRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "PlanChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanChangeDecisions_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralDecisions_CompanyId",
                table: "JobDeferralDecisions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralDecisions_RequestId",
                table: "JobDeferralDecisions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralDecisions_VesselId",
                table: "JobDeferralDecisions",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralRequests_CompanyId",
                table: "JobDeferralRequests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralRequests_PlannedJobId",
                table: "JobDeferralRequests",
                column: "PlannedJobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobDeferralRequests_VesselId",
                table: "JobDeferralRequests",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeDecisions_CompanyId",
                table: "PlanChangeDecisions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeDecisions_RequestId",
                table: "PlanChangeDecisions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeDecisions_VesselId",
                table: "PlanChangeDecisions",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeRequests_CompanyId",
                table: "PlanChangeRequests",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeRequests_MachineryItemId",
                table: "PlanChangeRequests",
                column: "MachineryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeRequests_PlannedJobId",
                table: "PlanChangeRequests",
                column: "PlannedJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeRequests_VesselId",
                table: "PlanChangeRequests",
                column: "VesselId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobDeferralDecisions");

            migrationBuilder.DropTable(
                name: "PlanChangeDecisions");

            migrationBuilder.DropTable(
                name: "JobDeferralRequests");

            migrationBuilder.DropTable(
                name: "PlanChangeRequests");

            migrationBuilder.DropColumn(
                name: "DeferredUntil",
                table: "PlannedJobs");
        }
    }
}
