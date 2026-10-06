using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// notification_preferences: per-person overrides of whether each notification kind is emailed (the
    /// notification dispatcher). Org RLS through the engineer, like every per-person table.
    /// </summary>
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    engineer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => new { x.engineer_id, x.kind });
                    table.ForeignKey(
                        name: "fk_notification_preferences_engineers_engineer_id",
                        column: x => x.engineer_id,
                        principalTable: "engineers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("alter table public.notification_preferences enable row level security;");
            migrationBuilder.Sql("alter table public.notification_preferences force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_notification_preferences_organization on public.notification_preferences for all
                    using (exists (select 1 from public.engineers e where e.id = notification_preferences.engineer_id))
                    with check (exists (select 1 from public.engineers e where e.id = notification_preferences.engineer_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences");
        }
    }
}
