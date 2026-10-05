using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Organizations;
using Pulse.Domain.Projects;
using Pulse.Domain.Teams;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 1b: org-wide settings tables carry an organization, and team names / project
/// codes are unique per organization rather than app-wide.
/// </summary>
[Collection("Integration")]
public class OrganizationSchemaTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private const string MigrationBefore = "20261005183221_RequireOrganizationIdAndOrgSessionVar";
    private const string MigrationUnderTest = "20261005194729_ScopeSettingsAndUniquenessToOrganization";
    private static readonly string[] SettingsTables =
        ["threshold_settings", "department_overwork_thresholds", "google_chat_spaces", "failed_emails"];

    public OrganizationSchemaTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Pre_existing_settings_rows_are_assigned_to_the_default_organization()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(Factory.ConnectionString);

        await scratch.MigrateToAsync(MigrationBefore);
        var before = new Dictionary<string, long>();
        foreach (var table in SettingsTables)
        {
            await scratch.InsertRowsAsync(table, count: 2);
            // Counted rather than assumed: earlier migrations seed some of these (e.g. threshold defaults).
            before[table] = await scratch.ScalarAsync($"SELECT count(*) FROM {table}");
        }

        await scratch.MigrateToAsync(MigrationUnderTest);

        foreach (var table in SettingsTables)
        {
            (await scratch.ScalarAsync($"SELECT count(*) FROM {table} WHERE organization_id = '{Organization.DefaultId}'"))
                .Should().Be(before[table], $"every pre-existing {table} row should belong to the default org");
            (await scratch.ScalarAsync($"SELECT count(*) FROM {table}"))
                .Should().Be(before[table], $"the migration shouldn't add or drop {table} rows");
        }
    }

    [Fact]
    public async Task Two_organizations_can_each_have_a_team_with_the_same_name()
    {
        var otherOrg = await SeedOrganizationAsync();

        await AddAsync(Team.Create("Shared Team Name"));
        var act = () => AddAsync(Team.Create("Shared Team Name"), otherOrg);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Team_names_are_still_unique_within_one_organization()
    {
        await AddAsync(Team.Create("Duplicate Team Name"));
        var act = () => AddAsync(Team.Create("Duplicate Team Name"));

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Two_organizations_can_each_have_a_project_with_the_same_code()
    {
        var otherOrg = await SeedOrganizationAsync();

        await AddAsync(Project.Create("Shared code A", code: "SHARED"));
        var act = () => AddAsync(Project.Create("Shared code B", code: "SHARED"), otherOrg);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Project_codes_are_still_unique_within_one_organization()
    {
        await AddAsync(Project.Create("Dup code A", code: "DUPCODE"));
        var act = () => AddAsync(Project.Create("Dup code B", code: "DUPCODE"));

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Threshold_settings_read_and_write_only_the_default_organizations_values()
    {
        var otherOrg = await SeedOrganizationAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.ThresholdSettings.Add(new ThresholdSetting { OrganizationId = otherOrg, Key = "org_scope_probe", Value = "other" });
            await db.SaveChangesAsync();
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IThresholdRepository>();
            (await repo.LoadAllAsync()).Should().NotContainKey("org_scope_probe");

            await repo.SetAsync("org_scope_probe", "default");
            (await repo.LoadAllAsync())["org_scope_probe"].Should().Be("default");
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.ThresholdSettings.SingleAsync(t => t.OrganizationId == otherOrg && t.Key == "org_scope_probe"))
                .Value.Should().Be("other", "writing the default org's setting must not touch another org's");
        }
    }

    [Fact]
    public async Task Department_overrides_read_and_write_only_the_default_organizations_values()
    {
        var otherOrg = await SeedOrganizationAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.DepartmentThresholdOverrides.Add(new DepartmentThresholdOverride
            {
                OrganizationId = otherOrg, Department = "OrgScopeDept", MaxConcurrentTasks = 9,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IDepartmentThresholdRepository>();
            (await repo.GetByDepartmentAsync("OrgScopeDept")).Should().BeNull();
            (await repo.GetAllAsync()).Should().NotContain(d => d.Department == "OrgScopeDept");

            await repo.UpsertAsync(new DepartmentThresholdOverride { Department = "OrgScopeDept", MaxConcurrentTasks = 3 });
            (await repo.GetByDepartmentAsync("OrgScopeDept"))!.MaxConcurrentTasks.Should().Be(3);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.DepartmentThresholdOverrides.SingleAsync(d => d.OrganizationId == otherOrg && d.Department == "OrgScopeDept"))
                .MaxConcurrentTasks.Should().Be(9, "upserting the default org's override must not touch another org's");
        }
    }

    private async Task<Guid> SeedOrganizationAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var org = Organization.Create("Other org", $"other-{Guid.NewGuid():N}"[..20]);
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    /// <summary>Saves the entity, optionally into another organization — OrganizationId has a private
    /// setter until Phase 2, so tests set it through EF's change tracker.</summary>
    private async Task AddAsync<T>(T entity, Guid? organizationId = null) where T : class
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Add(entity);
        if (organizationId is Guid orgId)
            db.Entry(entity).Property("OrganizationId").CurrentValue = orgId;
        await db.SaveChangesAsync();
    }
}
