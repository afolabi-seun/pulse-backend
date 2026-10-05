using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Multi-tenancy Phase 1c: two organizations with the same role names, and org B must never see org A's
/// data — not in lists, not in reports, not by id. Org A's data all carries one unique marker string; each
/// list check first proves org A's own user *does* see the marker through that endpoint (so a passing
/// test can't just mean the endpoint returns nothing), then that org B's user with the same role doesn't.
/// </summary>
[Collection("Integration")]
public class OrganizationIsolationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public OrganizationIsolationTests(PulseWebApplicationFactory factory) : base(factory) { }

    public static TheoryData<string, string> ListEndpoints => new()
    {
        { Roles.HeadOfPmo, "/api/v1/projects" },
        { Roles.HeadOfPmo, "/api/v1/teams" },
        { Roles.HeadOfPmo, "/api/v1/engineers" },
        { Roles.HeadOfPmo, "/api/v1/tasks" },
        { Roles.HeadOfPmo, "/api/v1/sprints" },
        { Roles.HeadOfPmo, "/api/v1/reports/pmo" },
        { Roles.HeadOfPmo, "/api/v1/reports/leadership" },
        { Roles.ProjectManager, "/api/v1/projects" },
        { Roles.ProjectManager, "/api/v1/tasks" },
    };

    [Theory]
    [MemberData(nameof(ListEndpoints))]
    public async Task Org_B_never_sees_org_A_data_in_a_list(string role, string path)
    {
        var world = await SeedTwoOrganizationsAsync(role);

        var asOrgA = await (await AuthenticatedClientAsync(world.OrgAEmail)).GetAsync(path);
        asOrgA.StatusCode.Should().Be(HttpStatusCode.OK, $"{role} in org A should be able to call {path}");
        (await asOrgA.Content.ReadAsStringAsync()).Should().Contain(world.Marker,
            $"the control: org A's own {role} should see org A's data at {path}, or this test proves nothing");

        var asOrgB = await (await AuthenticatedClientAsync(world.OrgBEmail)).GetAsync(path);
        asOrgB.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asOrgB.Content.ReadAsStringAsync()).Should().NotContain(world.Marker,
            $"org B's {role} must not see any of org A's data at {path}");
    }

    [Theory]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.ProjectManager)]
    public async Task Org_B_cannot_open_org_A_resources_by_id(string role)
    {
        var world = await SeedTwoOrganizationsAsync(role);
        var orgA = await AuthenticatedClientAsync(world.OrgAEmail);
        var orgB = await AuthenticatedClientAsync(world.OrgBEmail);

        foreach (var path in new[]
        {
            $"/api/v1/projects/{world.ProjectId}/members",
            $"/api/v1/projects/{world.ProjectId}/activity",
            $"/api/v1/tasks/{world.TaskId}",
            $"/api/v1/teams/{world.TeamId}/throughput",
            $"/api/v1/engineers/{world.EngineerId}",
        })
        {
            (await orgA.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK, $"control: org A's {role} can open {path}");

            var response = await orgB.GetAsync(path);
            response.StatusCode.Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.Forbidden],
                $"org B's {role} must not be able to open org A's {path}");
            (await response.Content.ReadAsStringAsync()).Should().NotContain(world.Marker);
        }
    }

    [Fact]
    public async Task A_project_created_by_an_org_B_user_belongs_to_org_B()
    {
        var world = await SeedTwoOrganizationsAsync(Roles.HeadOfPmo);
        var orgB = await AuthenticatedClientAsync(world.OrgBEmail);

        var response = await orgB.PostAsJsonAsync("/api/v1/projects", new { name = $"Created in B {world.Marker}", ownerTeamId = world.OrgBTeamId });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>(); // unauthenticated: unfiltered
        db.Projects.Single(p => p.Name == $"Created in B {world.Marker}").OrganizationId.Should().Be(world.OrgBId);
    }

    [Fact]
    public async Task Org_B_can_reuse_a_project_code_that_org_A_already_has()
    {
        var world = await SeedTwoOrganizationsAsync(Roles.HeadOfPmo);
        var orgB = await AuthenticatedClientAsync(world.OrgBEmail);

        var response = await orgB.PostAsJsonAsync("/api/v1/projects", new { name = "Code reuse", code = world.ProjectCode, ownerTeamId = world.OrgBTeamId });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changing_thresholds_in_org_B_does_not_change_them_in_org_A()
    {
        var world = await SeedTwoOrganizationsAsync(Roles.HeadOfPmo);
        var orgA = await AuthenticatedClientAsync(world.OrgAEmail);
        var orgB = await AuthenticatedClientAsync(world.OrgBEmail);
        var orgABefore = await MaxConcurrentTasksAsync(orgA);

        var patch = await orgB.PatchAsJsonAsync("/api/v1/thresholds", new { maxConcurrentTasks = orgABefore + 7 });
        patch.IsSuccessStatusCode.Should().BeTrue(await patch.Content.ReadAsStringAsync());

        (await MaxConcurrentTasksAsync(orgB)).Should().Be(orgABefore + 7);
        (await MaxConcurrentTasksAsync(orgA)).Should().Be(orgABefore, "org A's thresholds must be untouched by org B's change");
    }

    private static async Task<int> MaxConcurrentTasksAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/thresholds");
        return body.GetProperty("data").GetProperty("maxConcurrentTasks").GetInt32();
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record World(
        string Marker, Guid OrgBId, string OrgAEmail, string OrgBEmail,
        Guid TeamId, Guid ProjectId, string ProjectCode, Guid TaskId, Guid EngineerId, Guid OrgBTeamId);

    /// <summary>
    /// Org A is the default org; org B is new. Each gets one user with <paramref name="role"/>. Org A also
    /// gets a team, an engineer on it, a project owned by it (with the role user as a member), a sprint
    /// and an assigned task — all named with the marker. Seeding runs without an HTTP caller, so nothing
    /// is filtered or re-stamped; org B's user is moved into org B through the change tracker.
    /// </summary>
    private async Task<World> SeedTwoOrganizationsAsync(string role)
    {
        var marker = $"ORGA{Guid.NewGuid():N}"[..16];
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Guid orgBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var orgB = Organization.Create("Org B", $"org-b-{suffix}");
            db.Organizations.Add(orgB);
            await db.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        var orgAEmail = $"orga_{role}_{suffix}@pulse.io";
        var orgBEmail = $"orgb_{role}_{suffix}@pulse.io";
        var orgAUser = await SeedEngineerAsync(orgAEmail, role);
        var orgBUser = await SeedEngineerAsync(orgBEmail, role);
        await MoveToOrganizationAsync(orgBUser, orgBId);
        var orgBTeam = await SeedTeamAsync($"Org B team {suffix}");
        await MoveToOrganizationAsync(orgBTeam, orgBId);

        var team = await SeedTeamAsync($"Team {marker}");
        var engineer = await SeedNamedEngineerAsync($"Engineer {marker}", $"orga_eng_{suffix}@pulse.io", team.Id);
        var code = $"A{suffix}"[..8].ToUpperInvariant();
        var project = await SeedProjectAsync($"Project {marker}", ownerTeamId: team.Id, code: code);
        await SeedProjectMemberAsync(project.Id, orgAUser.Id);
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        await SeedSprintAsync(team.Id, $"Sprint {marker}", project.Id);
        var task = await SeedTaskAsync($"Task {marker}", project.Id, assigneeId: engineer.Id);

        return new World(marker, orgBId, orgAEmail, orgBEmail, team.Id, project.Id, code, task.Id, engineer.Id, orgBTeam.Id);
    }

    private async Task<Engineer> SeedNamedEngineerAsync(string name, string email, Guid teamId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var engineer = Engineer.Create(name, email, hasher.Hash("Str0ng!Pass12"), Roles.Engineer, 20, 14);
        engineer.AssignToTeam(teamId);
        db.Engineers.Add(engineer);
        await db.SaveChangesAsync();
        return engineer;
    }

    /// <summary>OrganizationId has a private setter until Phase 2, so seeding moves rows through EF's
    /// change tracker.</summary>
    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }
}
