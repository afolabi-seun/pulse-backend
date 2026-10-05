using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 1d: the organization boundary as a database-level backstop behind the EF Core
    /// query filters (Phase 1c). Every policy keys on app.org_visible(org): true when no organization is
    /// stamped on the connection (background jobs as 'service', and anonymous requests such as login, which
    /// must find an engineer before any org is known), otherwise only that organization's rows.
    /// HttpRlsContext stamps Guid.Empty for an authenticated caller without an org, so that fails closed.
    ///
    /// Two shapes, so existing access rules are untouched:
    ///   - Tables that already had RLS (projects, tasks, epics, wiki_pages, sprints) get an extra RESTRICTIVE
    ///     policy, AND-ed with their existing permissive ones — app.can_access_project() and friends are not
    ///     modified (see FixCanAccessProjectRecursion for why touching them is risky). Their children
    ///     (task_comments, task_history, …, wiki_page_revisions) already chain through the parent's
    ///     visibility, so they inherit the org boundary with no change.
    ///   - Tables with no RLS until now get RLS with a single permissive policy that is org-only, so within
    ///     an org nothing changes.
    ///
    /// A project's org is read from public.project_organizations, a trigger-maintained mirror of
    /// projects.organization_id with no RLS of its own (the same way project_members sidesteps RLS). Reading
    /// public.projects from inside a tasks policy instead would also apply projects' permissive rules, so an
    /// assignee who isn't a project member — allowed by rls_tasks — would lose their own task.
    ///
    /// Left without RLS, covered by the EF filters only: subtasks, escalation_events, automation_executions
    /// and sprint_retrospectives — their parents' (tasks', sprints') RLS also encodes access rules beyond
    /// org, so chaining through them would change who can see them within an org.
    /// </summary>
    public partial class AddOrganizationRowLevelSecurity : Migration
    {
        // Tables that already have RLS: (table, restrictive org predicate).
        private static readonly (string Table, string Predicate)[] Restrictive =
        [
            ("projects", "app.org_visible(organization_id)"),
            ("tasks", "app.project_org_visible(project_id)"),
            ("epics", "app.project_org_visible(project_id)"),
            ("wiki_pages", "app.project_org_visible(project_id)"),
            ("sprints", "exists (select 1 from public.teams t where t.id = sprints.team_id)"),
        ];

        // Tables getting RLS for the first time: (table, org-only predicate).
        private static readonly (string Table, string Predicate)[] NewlyProtected =
        [
            ("organizations", "app.org_visible(id)"),
            ("teams", "app.org_visible(organization_id)"),
            ("engineers", "app.org_visible(organization_id)"),
            ("threshold_settings", "app.org_visible(organization_id)"),
            ("department_overwork_thresholds", "app.org_visible(organization_id)"),
            ("google_chat_spaces", "app.org_visible(organization_id)"),
            ("failed_emails", "app.org_visible(organization_id)"),
            // Through an engineer (engineers' own policy is org-only, so EXISTS means "same org").
            ("refresh_tokens", "exists (select 1 from public.engineers e where e.id = refresh_tokens.engineer_id)"),
            ("notifications", "exists (select 1 from public.engineers e where e.id = notifications.user_id)"),
            ("audit_log", "exists (select 1 from public.engineers e where e.id = audit_log.actor_id)"),
            ("check_ins", "exists (select 1 from public.engineers e where e.id = check_ins.engineer_id)"),
            ("time_entries", "exists (select 1 from public.engineers e where e.id = time_entries.engineer_id)"),
            ("active_timers", "exists (select 1 from public.engineers e where e.id = active_timers.engineer_id)"),
            ("feedback", "exists (select 1 from public.engineers e where e.id = feedback.engineer_id)"),
            ("vitals_responses", "exists (select 1 from public.engineers e where e.id = vitals_responses.engineer_id)"),
            ("overwork_overrides", "exists (select 1 from public.engineers e where e.id = overwork_overrides.engineer_id)"),
            ("alert_rules", "exists (select 1 from public.engineers e where e.id = alert_rules.owner_engineer_id)"),
            ("automation_rules", "exists (select 1 from public.engineers e where e.id = automation_rules.owner_engineer_id)"),
            // Through a project, via the mirror.
            ("project_members", "app.project_org_visible(project_id)"),
            ("project_follows", "app.project_org_visible(project_id)"),
            // Through a team (teams' own policy is org-only).
            ("weekly_reports", "exists (select 1 from public.teams t where t.id = weekly_reports.team_id)"),
            // Through an alert rule (alert_rules' policy above is org-only).
            ("alert_conversations", "exists (select 1 from public.alert_rules r where r.id = alert_conversations.alert_rule_id)"),
            ("google_chat_threads", "exists (select 1 from public.alert_rules r where r.id = google_chat_threads.alert_rule_id)"),
            ("alert_metric_snapshots",
                "(scope_type = 'Team' and exists (select 1 from public.teams t where t.id = alert_metric_snapshots.scope_id)) " +
                "or (scope_type = 'Project' and app.project_org_visible(scope_id))"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.org_visible(org uuid) returns boolean
                language sql stable as $$
                    select app.current_org_id() is null or org = app.current_org_id()
                $$;");

            // ── Project → organization mirror ─────────────────────────────────────
            migrationBuilder.Sql(@"
                create table public.project_organizations (
                    project_id uuid primary key references public.projects (id) on delete cascade,
                    organization_id uuid not null references public.organizations (id) on delete restrict
                );");

            // projects is FORCE ROW LEVEL SECURITY and this migration connects as its owner, so the backfill
            // would silently read zero rows (see AddProjectCodeAndTaskNumber). Lift FORCE for this transaction.
            migrationBuilder.Sql("alter table public.projects no force row level security;");
            migrationBuilder.Sql(@"
                insert into public.project_organizations (project_id, organization_id)
                select id, organization_id from public.projects;");
            migrationBuilder.Sql("alter table public.projects force row level security;");

            // Reads only NEW/OLD — never queries projects — so it isn't subject to projects' RLS.
            migrationBuilder.Sql(@"
                create or replace function app.sync_project_organization() returns trigger
                language plpgsql as $$
                begin
                    insert into public.project_organizations (project_id, organization_id)
                    values (new.id, new.organization_id)
                    on conflict (project_id) do update set organization_id = excluded.organization_id;
                    return new;
                end
                $$;");
            migrationBuilder.Sql(@"
                create trigger trg_projects_sync_organization
                    after insert or update of organization_id on public.projects
                    for each row execute function app.sync_project_organization();");

            migrationBuilder.Sql(@"
                create or replace function app.project_org_visible(pid uuid) returns boolean
                language sql stable as $$
                    select app.org_visible(
                        (select po.organization_id from public.project_organizations po where po.project_id = pid))
                $$;");

            // ── Policies ──────────────────────────────────────────────────────────
            foreach (var (table, predicate) in Restrictive)
            {
                migrationBuilder.Sql($@"
                    create policy rls_{table}_organization on public.{table} as restrictive for all
                        using ({predicate}) with check ({predicate});");
            }

            foreach (var (table, predicate) in NewlyProtected)
            {
                migrationBuilder.Sql($"alter table public.{table} enable row level security;");
                migrationBuilder.Sql($"alter table public.{table} force row level security;");
                migrationBuilder.Sql($@"
                    create policy rls_{table}_organization on public.{table} for all
                        using ({predicate}) with check ({predicate});");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in NewlyProtected)
            {
                migrationBuilder.Sql($"drop policy if exists rls_{table}_organization on public.{table};");
                migrationBuilder.Sql($"alter table public.{table} no force row level security;");
                migrationBuilder.Sql($"alter table public.{table} disable row level security;");
            }

            foreach (var (table, _) in Restrictive)
                migrationBuilder.Sql($"drop policy if exists rls_{table}_organization on public.{table};");

            migrationBuilder.Sql("drop function if exists app.project_org_visible(uuid);");
            migrationBuilder.Sql("drop trigger if exists trg_projects_sync_organization on public.projects;");
            migrationBuilder.Sql("drop function if exists app.sync_project_organization();");
            migrationBuilder.Sql("drop table if exists public.project_organizations;");
            migrationBuilder.Sql("drop function if exists app.org_visible(uuid);");
        }
    }
}
