using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Broadens sprint visibility from team-only to also include project membership. Engineers can be
    /// members of projects run by other teams, so a sprint must be visible if it contains a task the
    /// caller can already see (delegated to the tasks table's own RLS — which encodes project membership
    /// plus the assignee path). Replaces the team-only policy from AddRowLevelSecurityRemainingTables.
    /// </summary>
    public partial class AddSprintProjectVisibilityRls : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop policy if exists rls_sprints on public.sprints;");
            migrationBuilder.Sql(@"
                create policy rls_sprints on public.sprints for all
                    using (
                        app.can_access_team(team_id)
                        or exists (select 1 from public.tasks t where t.sprint_id = sprints.id)
                    )
                    with check (
                        app.can_access_team(team_id)
                        or exists (select 1 from public.tasks t where t.sprint_id = sprints.id)
                    );");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the team-only policy from AddRowLevelSecurityRemainingTables.
            migrationBuilder.Sql("drop policy if exists rls_sprints on public.sprints;");
            migrationBuilder.Sql(@"
                create policy rls_sprints on public.sprints for all
                    using (app.can_access_team(team_id))
                    with check (app.can_access_team(team_id));");
        }
    }
}
