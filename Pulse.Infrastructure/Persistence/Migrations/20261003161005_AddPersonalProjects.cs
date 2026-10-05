using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Personal projects: one private project per person (HR/Accountant) that holds their own to-do tasks.
    /// <see cref="Pulse.Domain.Projects.Project.PersonalOwnerId"/> marks it.
    ///
    /// Row-level security: (1) the owner can create and read their own personal project row even before
    /// they are a member — a plain member-only policy would reject the very insert that creates it; and
    /// (2) the org-wide read-only SELECT policies added by AddOrgReadOnlyRlsPolicies are narrowed so
    /// Executive/HR/Accountant can no longer read ANOTHER person's personal project, tasks, epics or wiki
    /// pages. Their projects policy now excludes personal rows, and the project-scoped ones require the
    /// parent project to be visible to the caller (the subquery is itself row-level-filtered, so a
    /// personal project is simply not found).
    /// </summary>
    public partial class AddPersonalProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "personal_owner_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_projects_personal_owner_id",
                table: "projects",
                column: "personal_owner_id",
                unique: true,
                filter: "personal_owner_id is not null");

            migrationBuilder.Sql(@"
                create policy rls_projects_personal_owner on public.projects for all
                    using (personal_owner_id = app.current_user_id())
                    with check (personal_owner_id = app.current_user_id());");

            migrationBuilder.Sql("drop policy if exists rls_projects_org_read_only on public.projects;");
            migrationBuilder.Sql(@"
                create policy rls_projects_org_read_only on public.projects for select
                    using (app.is_org_read_only_role() and personal_owner_id is null);");

            foreach (var table in ProjectScopedTables)
            {
                migrationBuilder.Sql($"drop policy if exists rls_{table}_org_read_only on public.{table};");
                migrationBuilder.Sql($@"
                    create policy rls_{table}_org_read_only on public.{table} for select
                        using (app.is_org_read_only_role()
                               and exists (select 1 from public.projects p where p.id = {table}.project_id));");
            }
        }

        private static readonly string[] ProjectScopedTables = ["tasks", "epics", "wiki_pages"];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop policy if exists rls_projects_personal_owner on public.projects;");
            migrationBuilder.Sql("drop policy if exists rls_projects_org_read_only on public.projects;");
            migrationBuilder.Sql(@"
                create policy rls_projects_org_read_only on public.projects for select
                    using (app.is_org_read_only_role());");
            foreach (var table in ProjectScopedTables)
            {
                migrationBuilder.Sql($"drop policy if exists rls_{table}_org_read_only on public.{table};");
                migrationBuilder.Sql($@"
                    create policy rls_{table}_org_read_only on public.{table} for select
                        using (app.is_org_read_only_role());");
            }

            migrationBuilder.DropIndex(
                name: "ux_projects_personal_owner_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "personal_owner_id",
                table: "projects");
        }
    }
}
