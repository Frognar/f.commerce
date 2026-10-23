using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Catalog
{
    /// <inheritdoc />
    public partial class RemoveSearchDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "search_document",
                schema: "catalog",
                table: "products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "search_document",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
