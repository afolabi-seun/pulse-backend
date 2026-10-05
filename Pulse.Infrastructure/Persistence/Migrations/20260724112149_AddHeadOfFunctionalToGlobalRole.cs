using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// AddRowLevelSecurity's app.is_global_role() allowlist was never updated when Head of Functional
    /// was added to ProjectAccessPolicy.DepartmentHeadRoles alongside HeadOfRnD/HeadOfDesign — both of
    /// which already have a (loose, but functional) entry in this SQL list. Since head_of_functional had
    /// none, Postgres's RLS backstop treated it as a non-member, non-global role: any task not personally
    /// assigned to that actor was silently stripped by the rls_tasks USING clause before the C# app-level
    /// ProjectAccessPolicy check ever ran, surfacing as a plain "task not found" with no error. This adds
    /// the missing entry so Head of Functional gets the same DB-level visibility as its sibling dept-head
    /// roles.
    /// </summary>
    public partial class AddHeadOfFunctionalToGlobalRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.is_global_role() returns boolean
                language sql stable as $$
                    select current_setting('app.current_role', true) in (
                        'service', 'team_lead', 'product_manager', 'project_manager',
                        'head_of_pmo', 'head_of_rd', 'head_of_product', 'head_of_design', 'head_of_functional'
                    )
                $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.is_global_role() returns boolean
                language sql stable as $$
                    select current_setting('app.current_role', true) in (
                        'service', 'team_lead', 'product_manager', 'project_manager',
                        'head_of_pmo', 'head_of_rd', 'head_of_product', 'head_of_design'
                    )
                $$;");
        }
    }
}
