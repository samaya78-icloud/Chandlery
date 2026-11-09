using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RiskFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RiskAssessmentId",
                table: "JobDeferralRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskAssessmentLabel",
                table: "JobDeferralRequests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RiskRequiredForCriticalDeferral",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RiskAssessmentId",
                table: "JobDeferralRequests");

            migrationBuilder.DropColumn(
                name: "RiskAssessmentLabel",
                table: "JobDeferralRequests");

            migrationBuilder.DropColumn(
                name: "RiskRequiredForCriticalDeferral",
                table: "Companies");
        }
    }
}
