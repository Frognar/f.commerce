using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Documents
{
    /// <inheritdoc />
    public partial class DocumentOrdersAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "order_numbers",
                schema: "documents",
                table: "documents",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql(
                "UPDATE documents.documents SET order_numbers = json_build_array(order_number)::text WHERE order_number IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "order_number",
                schema: "documents",
                table: "documents");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at",
                schema: "documents",
                table: "outbox_messages",
                column: "processed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "documents");

            migrationBuilder.AddColumn<string>(
                name: "order_number",
                schema: "documents",
                table: "documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE documents.documents SET order_number = order_numbers::json->>0 WHERE order_numbers <> '[]';");

            migrationBuilder.DropColumn(
                name: "order_numbers",
                schema: "documents",
                table: "documents");
        }
    }
}
