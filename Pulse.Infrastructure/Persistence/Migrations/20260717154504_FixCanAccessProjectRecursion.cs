using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// UpdateRlsOwningTeamAccess added an "owning team" branch to app.can_access_project(pid) that reads
    /// public.projects.owner_team_id. public.projects is itself FORCE ROW LEVEL SECURITY-protected by a
    /// policy that calls app.can_access_project(id) — so any read of it from inside that function
    /// re-invokes the same function with the same argument, forever ("stack depth limit exceeded").
    ///
    /// An earlier version of this migration "fixed" that by routing the lookup through a SECURITY
    /// DEFINER function owned by a new NOLOGIN BYPASSRLS role — but creating a BYPASSRLS role itself
    /// requires the connecting role to already be a superuser (or hold BYPASSRLS), which only happens
    /// to be true in local dev/Testcontainers (where the DB role is a superuser). Against a properly
    /// locked-down non-superuser deployment role, that CREATE ROLE statement fails outright and blocks
    /// the whole migration pipeline (this took the API down in prod — see 2026-07-17 postmortem note in
    /// memory). Since a migration that has never successfully applied anywhere it matters can be edited
    /// in place rather than superseded, this emergency-reverts app.can_access_project to the pre-owning-
    /// team, two-branch form (project_members + is_global_role only) — a plain CREATE OR REPLACE
    /// FUNCTION that the existing migration-owning role can already do, no new role/privilege needed.
    ///
    /// This only weakens the DB-level RLS backstop's "engineer on the owning team" branch — the actual
    /// authorization users experience goes through the C# ProjectAccessPolicy.CanAccessProjectAsync,
    /// which independently implements the identical owning-team rule and is untouched by this. TODO:
    /// reintroduce the RLS-level owning-team check without recursion and without requiring superuser —
    /// e.g. a denormalized, trigger-synced, non-RLS lookup table mirroring projects.owner_team_id
    /// (mirroring how project_members already sidesteps RLS by not having any).
    /// </summary>
    public partial class FixCanAccessProjectRecursion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the (recursive, superuser-requiring) version from UpdateRlsOwningTeamAccess.
            // Not expected to actually be run — kept for historical accuracy of what Up() undoes.
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
    }
}
