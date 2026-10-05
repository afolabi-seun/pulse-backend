using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Defense-in-depth row-level security. The application layer (ProjectAccessPolicy) remains the
    /// primary, fine-grained gate; these policies are the coarse hard backstop that stops an individual
    /// contributor's query from ever touching another tenant's rows even if the app check were bypassed.
    ///
    /// Identity comes from the per-connection session settings stamped by RlsConnectionInterceptor:
    ///   app.current_role     — the caller's role, or 'service' for background work
    ///   app.current_user_id  — the caller's engineer id (empty for service / unauthenticated)
    ///
    /// Model: managers/heads/'service' see everything; engineers and designers see only rows in projects
    /// they are a member of (plus tasks assigned to them). FORCE is required because the app connects as
    /// the table owner, which Postgres otherwise exempts from RLS.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Helper functions ──────────────────────────────────────────────
            migrationBuilder.Sql("create schema if not exists app;");

            migrationBuilder.Sql(@"
                create or replace function app.current_user_id() returns uuid
                language sql stable as $$
                    select nullif(current_setting('app.current_user_id', true), '')::uuid
                $$;");

            migrationBuilder.Sql(@"
                create or replace function app.is_global_role() returns boolean
                language sql stable as $$
                    select current_setting('app.current_role', true) in (
                        'service', 'team_lead', 'product_manager', 'project_manager',
                        'head_of_pmo', 'head_of_rd', 'head_of_product', 'head_of_design'
                    )
                $$;");

            // Membership lookup. project_members has no RLS, so this is safe to call from other
            // tables' policies without recursion.
            migrationBuilder.Sql(@"
                create or replace function app.can_access_project(pid uuid) returns boolean
                language sql stable as $$
                    select app.is_global_role()
                        or exists (
                            select 1 from public.project_members pm
                            where pm.project_id = pid and pm.engineer_id = app.current_user_id()
                        )
                $$;");

            // ── projects: the project itself ──────────────────────────────────
            migrationBuilder.Sql("alter table public.projects enable row level security;");
            migrationBuilder.Sql("alter table public.projects force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_projects on public.projects for all
                    using (app.can_access_project(id))
                    with check (app.can_access_project(id));");

            // ── tasks: project-scoped, plus the assignee always sees their own ─
            migrationBuilder.Sql("alter table public.tasks enable row level security;");
            migrationBuilder.Sql("alter table public.tasks force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_tasks on public.tasks for all
                    using (app.can_access_project(project_id) or assignee_id = app.current_user_id())
                    with check (app.can_access_project(project_id) or assignee_id = app.current_user_id());");

            // ── epics: project-scoped via project_id ───────────────────────────
            migrationBuilder.Sql("alter table public.epics enable row level security;");
            migrationBuilder.Sql("alter table public.epics force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_epics on public.epics for all
                    using (app.can_access_project(project_id))
                    with check (app.can_access_project(project_id));");

            // ── wiki_pages: project-scoped via project_id ──────────────────────
            migrationBuilder.Sql("alter table public.wiki_pages enable row level security;");
            migrationBuilder.Sql("alter table public.wiki_pages force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_wiki_pages on public.wiki_pages for all
                    using (app.can_access_project(project_id))
                    with check (app.can_access_project(project_id));");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "tasks", "epics", "wiki_pages", "projects" })
            {
                migrationBuilder.Sql($"drop policy if exists rls_{table} on public.{table};");
                migrationBuilder.Sql($"alter table public.{table} no force row level security;");
                migrationBuilder.Sql($"alter table public.{table} disable row level security;");
            }

            migrationBuilder.Sql("drop function if exists app.can_access_project(uuid);");
            migrationBuilder.Sql("drop function if exists app.is_global_role();");
            migrationBuilder.Sql("drop function if exists app.current_user_id();");
            migrationBuilder.Sql("drop schema if exists app;");
        }
    }
}
