using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class AddVesselGroupsAndPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Vessels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VesselGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VesselGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VesselGroups_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselGroups_VesselGroups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "VesselGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VesselModuleSwitches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: true),
                    Module = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VesselModuleSwitches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VesselModuleSwitches_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselModuleSwitches_VesselGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "VesselGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VesselModuleSwitches_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VesselPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    VesselId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanRequisition = table.Column<bool>(type: "boolean", nullable: true),
                    CanPurchaseDirect = table.Column<bool>(type: "boolean", nullable: true),
                    MasterApprovalLimitUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    MonthlyBudgetUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VesselPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VesselPolicies_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselPolicies_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselPolicies_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VesselPolicies_VesselGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "VesselGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VesselPolicies_Vessels_VesselId",
                        column: x => x.VesselId,
                        principalTable: "Vessels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vessels_GroupId",
                table: "Vessels",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselGroups_CompanyId_ParentId",
                table: "VesselGroups",
                columns: new[] { "CompanyId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_VesselGroups_ParentId",
                table: "VesselGroups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselModuleSwitches_CompanyId_GroupId_VesselId_Module",
                table: "VesselModuleSwitches",
                columns: new[] { "CompanyId", "GroupId", "VesselId", "Module" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VesselModuleSwitches_GroupId",
                table: "VesselModuleSwitches",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselModuleSwitches_VesselId",
                table: "VesselModuleSwitches",
                column: "VesselId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_CategoryId",
                table: "VesselPolicies",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_CompanyId_GroupId",
                table: "VesselPolicies",
                columns: new[] { "CompanyId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_CompanyId_VesselId",
                table: "VesselPolicies",
                columns: new[] { "CompanyId", "VesselId" });

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_DepartmentId",
                table: "VesselPolicies",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_GroupId",
                table: "VesselPolicies",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_VesselPolicies_VesselId",
                table: "VesselPolicies",
                column: "VesselId");

            migrationBuilder.AddForeignKey(
                name: "FK_Vessels_VesselGroups_GroupId",
                table: "Vessels",
                column: "GroupId",
                principalTable: "VesselGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Vessels_VesselGroups_GroupId",
                table: "Vessels");

            migrationBuilder.DropTable(
                name: "VesselModuleSwitches");

            migrationBuilder.DropTable(
                name: "VesselPolicies");

            migrationBuilder.DropTable(
                name: "VesselGroups");

            migrationBuilder.DropIndex(
                name: "IX_Vessels_GroupId",
                table: "Vessels");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Vessels");
        }
    }
}
