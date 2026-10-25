using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Ordering
{
    /// <inheritdoc />
    public partial class InitialOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ordering");

            migrationBuilder.CreateTable(
                name: "order_counters",
                schema: "ordering",
                columns: table => new
                {
                    prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_counters", x => new { x.prefix, x.year });
                });

            migrationBuilder.CreateTable(
                name: "order_feed",
                schema: "ordering",
                columns: table => new
                {
                    position = table.Column<long>(type: "bigint", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_feed", x => x.position);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "ordering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    erp_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    customer_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    customer_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    salesperson_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    placed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    placed_by_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    placed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    net = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    gross = table.Column<decimal>(type: "numeric(15,2)", precision: 15, scale: 2, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    customer_reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_placed_at",
                schema: "ordering",
                table: "orders",
                columns: new[] { "customer_code", "placed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_erp_number",
                schema: "ordering",
                table: "orders",
                column: "erp_number");

            migrationBuilder.CreateIndex(
                name: "ix_orders_number",
                schema: "ordering",
                table: "orders",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_placed_at",
                schema: "ordering",
                table: "orders",
                column: "placed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_counters",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "order_feed",
                schema: "ordering");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "ordering");
        }
    }
}
