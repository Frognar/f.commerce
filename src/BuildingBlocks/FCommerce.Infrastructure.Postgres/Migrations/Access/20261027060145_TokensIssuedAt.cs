using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FCommerce.Infrastructure.Postgres.Migrations.Access
{
    /// <inheritdoc />
    public partial class TokensIssuedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A token now stores when it was issued; its expiry follows from its purpose (7 days, 1 hour).
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "issued_at",
                schema: "access",
                table: "one_time_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("""
                UPDATE access.one_time_tokens
                SET issued_at = expires_at - CASE purpose WHEN 'Invitation' THEN interval '7 days' ELSE interval '1 hour' END;
                """);

            migrationBuilder.DropColumn(
                name: "expires_at",
                schema: "access",
                table: "one_time_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                schema: "access",
                table: "one_time_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("""
                UPDATE access.one_time_tokens
                SET expires_at = issued_at + CASE purpose WHEN 'Invitation' THEN interval '7 days' ELSE interval '1 hour' END;
                """);

            migrationBuilder.DropColumn(
                name: "issued_at",
                schema: "access",
                table: "one_time_tokens");
        }
    }
}
