using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class OnboardApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedOnboardAt",
                table: "PurchaseOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedOnboardByName",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmergencyJustification",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ForReview",
                table: "PurchaseOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HandoverRequested",
                table: "PurchaseOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEmergency",
                table: "PurchaseOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "PurchaseOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedByName",
                table: "PurchaseOrders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsMaster",
                table: "CrewMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedOnboardAt",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ApprovedOnboardByName",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "EmergencyJustification",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ForReview",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "HandoverRequested",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "IsEmergency",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReviewedByName",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "IsMaster",
                table: "CrewMembers");
        }
    }
}
