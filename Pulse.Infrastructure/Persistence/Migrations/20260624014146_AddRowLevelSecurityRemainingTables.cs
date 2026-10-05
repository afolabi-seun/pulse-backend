using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Extends row-level security to the remaining tenant tables. Child tables delegate to their
    /// parent's RLS: a row is visible iff its parent task / wiki page is visible under that table's
    /// own policy (which already encodes membership + the task-assignee path). Sprints are team-scoped
    /// via the new app.can_access_team() helper. Builds on AddRowLevelSecurity (schema app + helpers).
    /// </summary>
    public partial class AddRowLevelSecurityRemainingTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Team membership helper for sprints (engineers see their own team; managers/service global).
            migrationBuilder.Sql(@"
                create or replace function app.can_access_team(tid uuid) returns boolean
                language sql stable as $$
                    select app.is_global_role()
                        or exists (
                            select 1 from public.engineers e
                            where e.id = app.current_user_id() and e.team_id = tid
                        )
                $$;");

            // ── Tables whose visibility follows their parent task ──────────────
            foreach (var (table, fk) in new[]
            {
                ("task_comments", "task_id"),
                ("task_history", "task_id"),
                ("task_estimation_sessions", "task_id"),
                ("task_estimation_votes", "task_id"),
                ("task_dependencies", "dependent_task_id"),
            })
            {
                migrationBuilder.Sql($"alter table public.{table} enable row level security;");
                migrationBuilder.Sql($"alter table public.{table} force row level security;");
                migrationBuilder.Sql($@"
                    create policy rls_{table} on public.{table} for all
                        using (exists (select 1 from public.tasks t where t.id = {fk}))
                        with check (exists (select 1 from public.tasks t where t.id = {fk}));");
            }

            // ── wiki_page_revisions: follows its parent wiki page ──────────────
            migrationBuilder.Sql("alter table public.wiki_page_revisions enable row level security;");
            migrationBuilder.Sql("alter table public.wiki_page_revisions force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_wiki_page_revisions on public.wiki_page_revisions for all
                    using (exists (select 1 from public.wiki_pages w where w.id = wiki_page_id))
                    with check (exists (select 1 from public.wiki_pages w where w.id = wiki_page_id));");

            // ── sprints: team-scoped ───────────────────────────────────────────
            migrationBuilder.Sql("alter table public.sprints enable row level security;");
            migrationBuilder.Sql("alter table public.sprints force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_sprints on public.sprints for all
                    using (app.can_access_team(team_id))
                    with check (app.can_access_team(team_id));");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
            {
                "task_comments", "task_history", "task_estimation_sessions",
                "task_estimation_votes", "task_dependencies", "wiki_page_revisions", "sprints",
            })
            {
                migrationBuilder.Sql($"drop policy if exists rls_{table} on public.{table};");
                migrationBuilder.Sql($"alter table public.{table} no force row level security;");
                migrationBuilder.Sql($"alter table public.{table} disable row level security;");
            }

            migrationBuilder.Sql("drop function if exists app.can_access_team(uuid);");
        }
    }
}
