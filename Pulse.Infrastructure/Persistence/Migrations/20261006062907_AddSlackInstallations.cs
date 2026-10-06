using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 2b: slack_installations — each organization's own Slack workspace connection.
    /// Org-only RLS like every other table (see AddOrganizationRowLevelSecurity); background work and the
    /// anonymous OAuth callback / events endpoint run with no org stamped, so they can resolve a workspace's
    /// organization by team id.
    /// </summary>
    public partial class AddSlackInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "slack_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    team_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bot_user_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    encrypted_bot_token = table.Column<string>(type: "text", nullable: false),
                    installed_by_engineer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_slack_installations", x => x.id);
                    table.ForeignKey(
                        name: "fk_slack_installations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_slack_installations_organization_id",
                table: "slack_installations",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_slack_installations_team_id",
                table: "slack_installations",
                column: "team_id",
                unique: true);

            migrationBuilder.Sql("alter table public.slack_installations enable row level security;");
            migrationBuilder.Sql("alter table public.slack_installations force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_slack_installations_organization on public.slack_installations for all
                    using (app.org_visible(organization_id)) with check (app.org_visible(organization_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "slack_installations");
        }
    }
}
