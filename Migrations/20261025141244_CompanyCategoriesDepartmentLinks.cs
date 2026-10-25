using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chandlery.Migrations
{
    /// <inheritdoc />
    public partial class CompanyCategoriesDepartmentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Categories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Categories",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DepartmentCategories",
                columns: table => new
                {
                    DepartmentId = table.Column<int>(type: "integer", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartmentCategories", x => new { x.DepartmentId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_DepartmentCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DepartmentCategories_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepartmentCategories_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_CompanyId",
                table: "Categories",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentCategories_CategoryId",
                table: "DepartmentCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentCategories_CompanyId",
                table: "DepartmentCategories",
                column: "CompanyId");

            // Categories used to be one shared tree. Give every existing category to the first company (the only one in
            // the development database); a database with several companies would need its shared tree cloned per company.
            migrationBuilder.Sql("UPDATE \"Categories\" SET \"CompanyId\" = (SELECT MIN(\"Id\") FROM \"Companies\") WHERE \"CompanyId\" = 0;");

            // So nothing stops working on upgrade, every existing department starts linked to all the top-level categories
            // of its company (which covers everything beneath them). The company can then narrow each department down.
            migrationBuilder.Sql(
                "INSERT INTO \"DepartmentCategories\" (\"DepartmentId\", \"CategoryId\", \"CompanyId\") " +
                "SELECT d.\"Id\", c.\"Id\", d.\"CompanyId\" FROM \"Departments\" d " +
                "JOIN \"Categories\" c ON c.\"CompanyId\" = d.\"CompanyId\" AND c.\"ParentCategoryId\" IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_Companies_CompanyId",
                table: "Categories",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Categories_Companies_CompanyId",
                table: "Categories");

            migrationBuilder.DropTable(
                name: "DepartmentCategories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_CompanyId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Categories");
        }
    }
}
