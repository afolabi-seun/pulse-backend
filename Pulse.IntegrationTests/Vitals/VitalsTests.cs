using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Vitals;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Vitals;

[Collection("Integration")]
public class VitalsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public VitalsTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task GetMyVitalsHistory_returns_only_the_callers_submissions()
    {
        await SeedEngineerAsync("vitals_me@vitals.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("vitals_me@vitals.io");

        // Another engineer's submission must not appear in the caller's history.
        await SeedEngineerAsync("vitals_other@vitals.io", Roles.Engineer);
        var otherClient = await AuthenticatedClientAsync("vitals_other@vitals.io");
        (await otherClient.PostAsJsonAsync("/api/v1/vitals", new { score = 2, comment = "theirs" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PostAsJsonAsync("/api/v1/vitals", new { score = 5, comment = "mine" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.GetAsync("/api/v1/vitals/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<VitalsDto>>>(JsonOpts);
        body!.Data!.Should().ContainSingle();
        body.Data![0].Comment.Should().Be("mine");
    }

    [Fact]
    public async Task My_vitals_history_requires_authentication()
    {
        var response = await Client.GetAsync("/api/v1/vitals/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Department_head_sees_only_their_own_departments_vitals()
    {
        var rndTeam    = await SeedTeamAsync("Pulse RnD team", null, department: "RnD");
        var designTeam = await SeedTeamAsync("Pulse Design team", null, department: "Design");
        var rndEng     = await SeedEngineerAsync("vitals_rnd_eng@vitals.io", Roles.Engineer);
        var designEng  = await SeedEngineerAsync("vitals_design_eng@vitals.io", Roles.Engineer);
        var head       = await SeedEngineerAsync("vitals_rnd_head@vitals.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(rndEng.Id, rndTeam.Id);
        await AssignEngineerToTeamAsync(designEng.Id, designTeam.Id);
        await AssignEngineerToTeamAsync(head.Id, rndTeam.Id);

        (await (await AuthenticatedClientAsync("vitals_rnd_eng@vitals.io"))
            .PostAsJsonAsync("/api/v1/vitals", new { score = 4, comment = "rnd" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await (await AuthenticatedClientAsync("vitals_design_eng@vitals.io"))
            .PostAsJsonAsync("/api/v1/vitals", new { score = 2, comment = "design" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await (await AuthenticatedClientAsync("vitals_rnd_head@vitals.io")).GetAsync("/api/v1/vitals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<VitalsDto>>>(JsonOpts);
        body!.Data!.Select(p => p.Comment).Should().Contain("rnd").And.NotContain("design");
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Executive)]
    public async Task Executive_and_hr_see_vitals_org_wide_across_departments(string role)
    {
        var rndTeam    = await SeedTeamAsync($"Pulse {role} RnD team", null, department: "RnD");
        var designTeam = await SeedTeamAsync($"Pulse {role} Design team", null, department: "Design");
        var rndEng     = await SeedEngineerAsync($"vitals_{role}_rnd_eng@vitals.io", Roles.Engineer);
        var designEng  = await SeedEngineerAsync($"vitals_{role}_design_eng@vitals.io", Roles.Engineer);
        await SeedEngineerAsync($"vitals_{role}_viewer@vitals.io", role);
        await AssignEngineerToTeamAsync(rndEng.Id, rndTeam.Id);
        await AssignEngineerToTeamAsync(designEng.Id, designTeam.Id);

        (await (await AuthenticatedClientAsync($"vitals_{role}_rnd_eng@vitals.io"))
            .PostAsJsonAsync("/api/v1/vitals", new { score = 4, comment = $"{role}-rnd" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await (await AuthenticatedClientAsync($"vitals_{role}_design_eng@vitals.io"))
            .PostAsJsonAsync("/api/v1/vitals", new { score = 2, comment = $"{role}-design" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await (await AuthenticatedClientAsync($"vitals_{role}_viewer@vitals.io")).GetAsync("/api/v1/vitals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<VitalsDto>>>(JsonOpts);
        body!.Data!.Select(p => p.Comment).Should().Contain(new[] { $"{role}-rnd", $"{role}-design" });
    }
}
