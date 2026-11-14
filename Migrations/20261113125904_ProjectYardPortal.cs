using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class ProjectYardPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "YardId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QuotedByYard",
                table: "ProjectTenders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ProjectApprovalRequests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Users_YardId",
                table: "Users",
                column: "YardId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_ProjectYards_YardId",
                table: "Users",
                column: "YardId",
                principalTable: "ProjectYards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_ProjectYards_YardId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_YardId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "YardId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuotedByYard",
                table: "ProjectTenders");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ProjectApprovalRequests");
        }
    }
}
