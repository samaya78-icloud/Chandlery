using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class RfqEvaluationDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvaluationNote",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "EvaluationUpdatedAt",
                table: "Rfqs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationUpdatedByName",
                table: "Rfqs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvaluationNote",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EvaluationUpdatedAt",
                table: "Rfqs");

            migrationBuilder.DropColumn(
                name: "EvaluationUpdatedByName",
                table: "Rfqs");
        }
    }
}
