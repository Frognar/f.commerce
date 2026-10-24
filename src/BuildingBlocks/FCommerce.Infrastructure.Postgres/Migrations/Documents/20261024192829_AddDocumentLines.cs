using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Documents
{
    /// <inheritdoc />
    public partial class AddDocumentLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lines",
                schema: "documents",
                table: "documents",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "reorderable_lines",
                schema: "documents",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lines",
                schema: "documents",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "reorderable_lines",
                schema: "documents",
                table: "documents");
        }
    }
}
