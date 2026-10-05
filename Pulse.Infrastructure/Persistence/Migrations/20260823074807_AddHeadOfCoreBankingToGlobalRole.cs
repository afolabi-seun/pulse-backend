using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Same fix as AddHeadOfFunctionalToGlobalRole, for the newly added Head of Core Banking role:
    /// app.is_global_role()'s allowlist must include every ProjectAccessPolicy.DepartmentHeadRoles
    /// member, or Postgres RLS silently strips any row not personally owned by the actor before the
    /// C# app-level ProjectAccessPolicy check ever runs — surfacing as a plain "not found", not an
    /// error. Adds head_of_core_banking to the same allowlist.
    /// </summary>
    public partial class AddHeadOfCoreBankingToGlobalRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.is_global_role() returns boolean
                language sql stable as $$
                    select current_setting('app.current_role', true) in (
                        'service', 'team_lead', 'product_manager', 'project_manager',
                        'head_of_pmo', 'head_of_rd', 'head_of_product', 'head_of_design', 'head_of_functional',
                        'head_of_core_banking'
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
                        'head_of_pmo', 'head_of_rd', 'head_of_product', 'head_of_design', 'head_of_functional'
                    )
                $$;");
        }
    }
}
