using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class ProjectYardDialogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProjectSuperintendentSignOff",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProjectNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectNotices_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectNotices_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTenderQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AskedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AskedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Answer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTenderQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTenderQuestions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTenderQuestions_ProjectTenders_TenderId",
                        column: x => x.TenderId,
                        principalTable: "ProjectTenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTenderRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    TotalUsd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ByYard = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTenderRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTenderRevisions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectTenderRevisions_ProjectTenders_TenderId",
                        column: x => x.TenderId,
                        principalTable: "ProjectTenders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotices_CompanyId_ReadAt_At",
                table: "ProjectNotices",
                columns: new[] { "CompanyId", "ReadAt", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotices_ProjectId",
                table: "ProjectNotices",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTenderQuestions_CompanyId",
                table: "ProjectTenderQuestions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTenderQuestions_TenderId",
                table: "ProjectTenderQuestions",
                column: "TenderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTenderRevisions_CompanyId",
                table: "ProjectTenderRevisions",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTenderRevisions_TenderId_Number",
                table: "ProjectTenderRevisions",
                columns: new[] { "TenderId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectNotices");

            migrationBuilder.DropTable(
                name: "ProjectTenderQuestions");

            migrationBuilder.DropTable(
                name: "ProjectTenderRevisions");

            migrationBuilder.DropColumn(
                name: "ProjectSuperintendentSignOff",
                table: "Companies");
        }
    }
}
