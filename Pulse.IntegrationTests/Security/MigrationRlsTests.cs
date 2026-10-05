using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Pulse.IntegrationTests.Security;

/// <summary>
/// A data-changing migration must actually change rows. The startup migration context runs outside any HTTP
/// request, so unless it identifies as the service role, FORCE ROW LEVEL SECURITY hides every row from a role
/// that does not bypass RLS and an UPDATE touches nothing while still being recorded as applied (the
/// FixQaSubtaskType incident). The test connection is a superuser, so — as in RlsTests — a non-superuser role is
/// switched to, which is what a production migration role without BYPASSRLS looks like to the policies.
/// </summary>
[Collection("Integration")]
public class MigrationRlsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public MigrationRlsTests(PulseWebApplicationFactory factory) : base(factory) { }

    private sealed class NoIdentity : IRlsContext
    {
        public (string Role, string UserId) Resolve() => (string.Empty, string.Empty);
    }

    private async Task<int> UpdateAsRlsBoundRoleAsync(IRlsContext identity, Guid taskId, string newTitle)
    {
        var options = new DbContextOptionsBuilder<PulseDbContext>()
            .UseNpgsql(Factory.ConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(identity))
            .Options;
        await using var db = new PulseDbContext(options);
        await db.Database.OpenConnectionAsync();

        // Same shape as a production migration role: not a superuser, no BYPASSRLS.
        await db.Database.ExecuteSqlRawAsync("""
            do $$ begin
                if not exists (select 1 from pg_roles where rolname = 'rls_migration_probe') then
                    create role rls_migration_probe nologin nosuperuser nobypassrls;
                end if;
            end $$;
            grant usage on schema app to rls_migration_probe;
            grant execute on all functions in schema app to rls_migration_probe;
            grant usage on schema public to rls_migration_probe;
            grant select, update on all tables in schema public to rls_migration_probe;
            set role rls_migration_probe;
            """);

        return await db.Database.ExecuteSqlRawAsync("UPDATE tasks SET title = {0} WHERE id = {1}", newTitle, taskId);
    }

    [Fact]
    public async Task A_migration_role_without_BYPASSRLS_updates_nothing_when_it_has_no_identity()
    {
        // The incident: the policy sees no role, so every row is filtered out and the UPDATE is a silent no-op.
        var project = await SeedProjectAsync("Migration RLS no-identity project");
        var task = await SeedTaskAsync("Original title", project.Id);

        var updated = await UpdateAsRlsBoundRoleAsync(new NoIdentity(), task.Id, "Changed");

        updated.Should().Be(0);
    }

    [Fact]
    public async Task The_migration_context_updates_rows_because_it_identifies_as_the_service_role()
    {
        var project = await SeedProjectAsync("Migration RLS service project");
        var task = await SeedTaskAsync("Original title", project.Id);

        var updated = await UpdateAsRlsBoundRoleAsync(new ServiceRlsContext(), task.Id, "Changed");

        updated.Should().Be(1);
    }
}
