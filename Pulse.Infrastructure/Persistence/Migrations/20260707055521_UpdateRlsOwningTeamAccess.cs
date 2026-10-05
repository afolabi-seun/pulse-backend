using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateRlsOwningTeamAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Extend can_access_project to match the application-layer ProjectAccessPolicy change:
            // engineers and designers on the team that owns the project now have implicit access,
            // even without an explicit row in project_members.
            migrationBuilder.Sql(@"
                create or replace function app.can_access_project(pid uuid) returns boolean
                language sql stable as $$
                    select app.is_global_role()
                        or exists (
                            select 1 from public.project_members pm
                            where pm.project_id = pid and pm.engineer_id = app.current_user_id()
                        )
                        or exists (
                            select 1 from public.projects p
                            join public.engineers e on e.team_id = p.owner_team_id
                            where p.id = pid and e.id = app.current_user_id()
                        )
                $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the member-only version.
            migrationBuilder.Sql(@"
                create or replace function app.can_access_project(pid uuid) returns boolean
                language sql stable as $$
                    select app.is_global_role()
                        or exists (
                            select 1 from public.project_members pm
                            where pm.project_id = pid and pm.engineer_id = app.current_user_id()
                        )
                $$;");
        }
    }
}
