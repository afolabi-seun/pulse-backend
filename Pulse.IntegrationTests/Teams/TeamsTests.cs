using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Teams;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Teams;

[Collection("Integration")]
public class TeamsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TeamsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_list_teams()
    {
        var response = await Client.GetAsync("/api/v1/teams");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_create_team()
    {
        await SeedEngineerAsync("teams_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("teams_eng_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Forbidden" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Product_manager_can_create_team()
    {
        // Team creation is [TeamCreatorOrAbove] = head_of_pmo, project_manager, head_of_product,
        // or product_manager.
        await SeedEngineerAsync("teams_pdm_create@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("teams_pdm_create@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "PdM can create" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Designer_cannot_create_team()
    {
        await SeedEngineerAsync("teams_designer_denied@pulse.io", Roles.Designer);
        var client = await AuthenticatedClientAsync("teams_designer_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Forbidden" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_can_list_teams()
    {
        await SeedEngineerAsync("teams_pm_list@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("teams_pm_list@pulse.io");

        var response = await client.GetAsync("/api/v1/teams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTeam_returns_201_with_team_data()
    {
        await SeedEngineerAsync("teams_head_create@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_head_create@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Platform Squad" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts);
        body!.Data!.Name.Should().Be("Platform Squad");
        body.Data.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTeam_returns_400_when_name_is_missing()
    {
        await SeedEngineerAsync("teams_validation@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_validation@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTeam_accepts_optional_team_lead()
    {
        var lead = await SeedEngineerAsync("teams_lead@pulse.io", Roles.TeamLead);
        await SeedEngineerAsync("teams_head_lead@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_head_lead@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/teams",
            new { name = "Lead Squad", teamLeadId = lead.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts);
        body!.Data!.TeamLeadId.Should().Be(lead.Id);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTeam_returns_200_with_team_data()
    {
        await SeedEngineerAsync("teams_get_head@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_get_head@pulse.io");
        var team = await SeedTeamAsync("Get Me Team");

        var response = await client.GetAsync($"/api/v1/teams/{team.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(team.Id);
        body.Data.Name.Should().Be("Get Me Team");
    }

    [Fact]
    public async Task GetTeam_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("teams_404@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("teams_404@pulse.io");

        var response = await client.GetAsync($"/api/v1/teams/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTeam_renames_team()
    {
        await SeedEngineerAsync("teams_rename@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_rename@pulse.io");
        var team = await SeedTeamAsync("Old Name Team");

        var response = await client.PatchAsJsonAsync($"/api/v1/teams/{team.Id}",
            new { name = "New Name Team" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts);
        body!.Data!.Name.Should().Be("New Name Team");
    }

    [Fact]
    public async Task UpdateTeam_deactivates_team()
    {
        await SeedEngineerAsync("teams_deactivate@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_deactivate@pulse.io");
        var team = await SeedTeamAsync("Active Team");

        var response = await client.PatchAsJsonAsync($"/api/v1/teams/{team.Id}",
            new { deactivate = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TeamDto>>(JsonOpts);
        body!.Data!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateTeam_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("teams_update_404@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("teams_update_404@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/teams/{Guid.NewGuid()}",
            new { name = "Ghost" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateTeam_returns_403_for_product_manager_role()
    {
        // [PmoOnly] admits head_of_pmo / project_manager; a product_manager is denied.
        await SeedEngineerAsync("teams_patch_pdm@pulse.io", Roles.ProductManager);
        var client = await AuthenticatedClientAsync("teams_patch_pdm@pulse.io");
        var team = await SeedTeamAsync("PdM Cannot Patch");

        var response = await client.PatchAsJsonAsync($"/api/v1/teams/{team.Id}", new { name = "X" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Executive_can_list_teams_and_read_throughput_but_cannot_create()
    {
        await SeedEngineerAsync("teams_exec@pulse.io", Roles.Executive);
        var client = await AuthenticatedClientAsync("teams_exec@pulse.io");
        var team = await SeedTeamAsync("Exec Read Team");

        var listResp = await client.GetAsync("/api/v1/teams");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var throughputResp = await client.GetAsync($"/api/v1/teams/{team.Id}/throughput");
        throughputResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var createResp = await client.PostAsJsonAsync("/api/v1/teams", new { name = "Exec Should Not Create" });
        createResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
