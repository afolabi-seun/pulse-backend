using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Executive, HR and Accountant are org-wide, READ-ONLY viewers, but app.is_global_role() — which
    /// every row-level-security policy keys off — never listed them. As a result, under the restricted
    /// runtime role Postgres silently stripped every task/project/sprint/wiki row from them: the
    /// Engineers and Reports pages listed engineers (the engineers table has no RLS) but showed 0
    /// tasks and 0 points, and Sprints/Wiki/Projects came back empty.
    ///
    /// They are deliberately NOT added to is_global_role(): its policies are FOR ALL, so that would
    /// also let these roles write at the database level. Instead this adds a separate
    /// app.is_org_read_only_role() and a SELECT-only policy per RLS-protected table. Permissive
    /// policies are OR-ed, so SELECT widens for these roles while INSERT/UPDATE/DELETE stay governed
    /// by the existing policies (and the application layer).
    /// </summary>
    public partial class AddOrgReadOnlyRlsPolicies : Migration
    {
        private static readonly string[] Tables =
        [
            "projects", "tasks", "epics", "wiki_pages", "wiki_page_revisions", "sprints",
            "task_comments", "task_history", "task_estimation_sessions", "task_estimation_votes",
            "task_dependencies",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.is_org_read_only_role() returns boolean
                language sql stable as $$
                    select current_setting('app.current_role', true) in ('executive', 'hr', 'accountant')
                $$;");

            foreach (var table in Tables)
            {
                migrationBuilder.Sql($@"
                    create policy rls_{table}_org_read_only on public.{table} for select
                        using (app.is_org_read_only_role());");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                migrationBuilder.Sql($"drop policy if exists rls_{table}_org_read_only on public.{table};");

            migrationBuilder.Sql("drop function if exists app.is_org_read_only_role();");
        }
    }
}
