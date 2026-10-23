using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Access
{
    /// <inheritdoc />
    public partial class AddMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "access",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    attributes = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    erp_contact_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.user_id, x.customer_code });
                    table.ForeignKey(
                        name: "FK_memberships_accounts_user_id",
                        column: x => x.user_id,
                        principalSchema: "access",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_customer_code",
                schema: "access",
                table: "memberships",
                column: "customer_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships",
                schema: "access");
        }
    }
}
