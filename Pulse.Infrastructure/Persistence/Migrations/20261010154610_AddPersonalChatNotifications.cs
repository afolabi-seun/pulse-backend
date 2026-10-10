using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Personal chat notifications: personal_chat_settings (each person's opt-in Slack / Google Chat channel
    /// and how to reach them there) and notification_preferences.chat_enabled (default on). Org RLS through
    /// the engineer, like every per-person table.
    /// </summary>
    public partial class AddPersonalChatNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "chat_enabled",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "personal_chat_settings",
                columns: table => new
                {
                    engineer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    slack_user_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    google_chat_dm_space = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_chat_settings", x => x.engineer_id);
                    table.ForeignKey(
                        name: "fk_personal_chat_settings_engineers_engineer_id",
                        column: x => x.engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("alter table public.personal_chat_settings enable row level security;");
            migrationBuilder.Sql("alter table public.personal_chat_settings force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_personal_chat_settings_organization on public.personal_chat_settings for all
                    using (exists (select 1 from public.engineers e where e.id = personal_chat_settings.engineer_id))
                    with check (exists (select 1 from public.engineers e where e.id = personal_chat_settings.engineer_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_chat_settings");

            migrationBuilder.DropColumn(
                name: "chat_enabled",
                table: "notification_preferences");
        }
    }
}
