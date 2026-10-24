using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class AddProductDiscontinued : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "discontinued",
                schema: "catalog",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discontinued",
                schema: "catalog",
                table: "products");
        }
    }
}
