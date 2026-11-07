using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class MasterDataSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DocType",
                table: "SyncDirties",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<bool>(
                name: "Deleted",
                table: "SyncDirties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Snapshot",
                table: "SyncDirties",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SyncDirties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNodeId",
                table: "DepartmentCategories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCreatedAt",
                table: "DepartmentCategories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncUpdatedAt",
                table: "DepartmentCategories",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "SyncVersion",
                table: "DepartmentCategories",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNodeId",
                table: "Currencies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCreatedAt",
                table: "Currencies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncUpdatedAt",
                table: "Currencies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "SyncVersion",
                table: "Currencies",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNodeId",
                table: "CompanyCurrencies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCreatedAt",
                table: "CompanyCurrencies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncUpdatedAt",
                table: "CompanyCurrencies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<int>(
                name: "SyncVersion",
                table: "CompanyCurrencies",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_SyncDirties_At",
                table: "SyncDirties",
                column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncDirties_At",
                table: "SyncDirties");

            migrationBuilder.DropColumn(
                name: "Deleted",
                table: "SyncDirties");

            migrationBuilder.DropColumn(
                name: "Snapshot",
                table: "SyncDirties");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SyncDirties");

            migrationBuilder.DropColumn(
                name: "OriginNodeId",
                table: "DepartmentCategories");

            migrationBuilder.DropColumn(
                name: "SyncCreatedAt",
                table: "DepartmentCategories");

            migrationBuilder.DropColumn(
                name: "SyncUpdatedAt",
                table: "DepartmentCategories");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "DepartmentCategories");

            migrationBuilder.DropColumn(
                name: "OriginNodeId",
                table: "Currencies");

            migrationBuilder.DropColumn(
                name: "SyncCreatedAt",
                table: "Currencies");

            migrationBuilder.DropColumn(
                name: "SyncUpdatedAt",
                table: "Currencies");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "Currencies");

            migrationBuilder.DropColumn(
                name: "OriginNodeId",
                table: "CompanyCurrencies");

            migrationBuilder.DropColumn(
                name: "SyncCreatedAt",
                table: "CompanyCurrencies");

            migrationBuilder.DropColumn(
                name: "SyncUpdatedAt",
                table: "CompanyCurrencies");

            migrationBuilder.DropColumn(
                name: "SyncVersion",
                table: "CompanyCurrencies");

            migrationBuilder.AlterColumn<string>(
                name: "DocType",
                table: "SyncDirties",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60);
        }
    }
}
