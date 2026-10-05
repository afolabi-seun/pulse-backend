using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the idle-timeout clock backing RefreshToken.IsIdleExpired. Backfills existing rows from
    /// created_at (rather than EF's default year-1 sentinel) so already-active sessions get a normal
    /// idle window measured from when they logged in, instead of being force-expired the instant this
    /// migration lands.
    /// </summary>
    public partial class AddRefreshTokenLastUsedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_used_at",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("UPDATE refresh_tokens SET last_used_at = created_at WHERE last_used_at IS NULL;");

            migrationBuilder.AlterColumn<DateTime>(
                name: "last_used_at",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "refresh_tokens");
        }
    }
}
