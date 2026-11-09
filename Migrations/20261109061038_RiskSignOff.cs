using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RiskSignOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RiskAssessments_CompanyId_Code_Version",
                table: "RiskAssessments");

            migrationBuilder.AddColumn<bool>(
                name: "IsRiskReviewer",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "RiskAssessments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedFromCode",
                table: "RiskAssessments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SharedFromId",
                table: "RiskAssessments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedFromVersion",
                table: "RiskAssessments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsRiskReviewer",
                table: "CrewMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RiskSignOffByAnotherPerson",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_CompanyId_Code_Version",
                table: "RiskAssessments",
                columns: new[] { "CompanyId", "Code", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RiskAssessments_CompanyId_Code_Version",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "IsRiskReviewer",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "SharedFromCode",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "SharedFromId",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "SharedFromVersion",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "IsRiskReviewer",
                table: "CrewMembers");

            migrationBuilder.DropColumn(
                name: "RiskSignOffByAnotherPerson",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_CompanyId_Code_Version",
                table: "RiskAssessments",
                columns: new[] { "CompanyId", "Code", "Version" },
                unique: true);
        }
    }
}
