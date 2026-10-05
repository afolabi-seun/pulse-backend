using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 0: the organizations table exists with the default org, and every team,
/// engineer and project — pre-existing or newly inserted — belongs to it.
/// </summary>
[Collection("Integration")]
public class OrganizationsMigrationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private const string MigrationBefore = "20261004024120_AddEstimationApprovalEscalation";
    private const string MigrationUnderTest = "20261005175241_AddOrganizations";
    private static readonly string[] RootTables = ["teams", "engineers", "projects"];

    public OrganizationsMigrationTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task The_default_organization_is_seeded()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        var org = await db.Organizations.SingleAsync(o => o.Id == Organization.DefaultId);

        org.Slug.Should().Be(Organization.DefaultSlug);
        org.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task New_teams_engineers_and_projects_land_in_the_default_organization()
    {
        var team = await SeedTeamAsync("Org Phase0 Team");
        var engineer = await SeedEngineerAsync("org_phase0@pulse.io");
        var project = await SeedProjectAsync("Org Phase0 Project");

        // EF reads the database-generated default back on insert.
        team.OrganizationId.Should().Be(Organization.DefaultId);
        engineer.OrganizationId.Should().Be(Organization.DefaultId);
        project.OrganizationId.Should().Be(Organization.DefaultId);
    }

    [Fact]
    public async Task Rows_that_existed_before_the_migration_are_assigned_to_the_default_organization()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(Factory.ConnectionString);

        await scratch.MigrateToAsync(MigrationBefore);
        foreach (var table in RootTables)
            await scratch.InsertRowsAsync(table, count: 3);

        await scratch.MigrateToAsync(MigrationUnderTest);

        foreach (var table in RootTables)
        {
            (await scratch.ScalarAsync($"SELECT count(*) FROM {table}")).Should().Be(3, table);
            (await scratch.ScalarAsync($"SELECT count(*) FROM {table} WHERE organization_id = '{Organization.DefaultId}'"))
                .Should().Be(3, $"every pre-existing {table} row should belong to the default org");
        }
    }

    [Fact]
    public async Task The_migration_rolls_back_cleanly()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(Factory.ConnectionString);

        await scratch.MigrateToAsync(MigrationUnderTest);
        await scratch.MigrateToAsync(MigrationBefore);

        (await scratch.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name = 'organizations'"))
            .Should().Be(0);
        (await scratch.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE column_name = 'organization_id'"))
            .Should().Be(0);
    }
}
