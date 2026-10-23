using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Documents
{
    /// <inheritdoc />
    public partial class InitialDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    customer_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sale_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    net = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    gross = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    paid = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: true),
                    outstanding = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    file_size = table.Column<long>(type: "bigint", nullable: true),
                    file_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_customer_issue_date",
                schema: "documents",
                table: "documents",
                columns: new[] { "customer_code", "issue_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents",
                schema: "documents");
        }
    }
}
