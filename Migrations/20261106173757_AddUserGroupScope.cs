using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class AddUserGroupScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserGroupScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SyncVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserGroupScopes_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupScopes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserGroupScopes_VesselGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "VesselGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupScopes_CompanyId",
                table: "UserGroupScopes",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupScopes_GroupId",
                table: "UserGroupScopes",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupScopes_UserId_GroupId",
                table: "UserGroupScopes",
                columns: new[] { "UserId", "GroupId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserGroupScopes");
        }
    }
}
