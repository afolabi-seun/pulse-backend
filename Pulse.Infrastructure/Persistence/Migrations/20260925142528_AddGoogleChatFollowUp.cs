using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleChatFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "google_chat_space_id",
                table: "alert_rules",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "google_chat_spaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_chat_spaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "google_chat_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    space_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    thread_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_chat_threads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_google_chat_spaces_space_id",
                table: "google_chat_spaces",
                column: "space_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_google_chat_threads_space_thread",
                table: "google_chat_threads",
                columns: new[] { "space_id", "thread_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "google_chat_spaces");

            migrationBuilder.DropTable(
                name: "google_chat_threads");

            migrationBuilder.DropColumn(
                name: "google_chat_space_id",
                table: "alert_rules");
        }
    }
}
