using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Engineers.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>The hand-off picker must work for the engineer doing the hand-off, not only PMO —
/// previously it was fed by a PM-only endpoint, so a backend engineer saw no frontend engineers.</summary>
[Collection("Integration")]
public class HandoffCandidatesTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public HandoffCandidatesTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<Engineer> SeedEngineerWithDisciplineAsync(string email, Discipline discipline, bool active = true)
    {
        var engineer = await SeedEngineerAsync(email, Roles.Engineer);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var tracked = await db.Engineers.FindAsync(engineer.Id);
        tracked!.SetDiscipline(discipline);
        if (!active) tracked.Deactivate();
        await db.SaveChangesAsync();
        return engineer;
    }

    private async Task<List<HandoffCandidateDto>> GetCandidatesAsync(HttpClient client, Guid taskId, string discipline)
    {
        var resp = await client.GetAsync($"/api/v1/tasks/{taskId}/handoff-candidates?discipline={discipline}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ApiResponse<List<HandoffCandidateDto>>>(JsonOpts))!.Data!;
    }

    [Fact]
    public async Task Backend_assignee_sees_every_active_frontend_engineer_with_the_projects_own_first()
    {
        var be = await SeedEngineerWithDisciplineAsync("hc_be@pulse.io", Discipline.Backend);
        var feMember = await SeedEngineerWithDisciplineAsync("hc_fe_member@pulse.io", Discipline.Frontend);
        var feNotOnProject = await SeedEngineerWithDisciplineAsync("hc_fe_other@pulse.io", Discipline.Frontend);
        var feInactive = await SeedEngineerWithDisciplineAsync("hc_fe_inactive@pulse.io", Discipline.Frontend, active: false);
        var otherBe = await SeedEngineerWithDisciplineAsync("hc_be_member@pulse.io", Discipline.Backend);

        var project = await SeedProjectAsync("Handoff Candidates Project");
        foreach (var e in new[] { be, feMember, feInactive, otherBe })
            await SeedProjectMemberAsync(project.Id, e.Id);
        var task = await SeedTaskAsync("Two-stage", project.Id, assigneeId: be.Id);

        var client = await AuthenticatedClientAsync("hc_be@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id, "frontend");

        // Org-wide: the frontend engineer who is not on the project is offered too (the hand-off adds
        // them), listed after the project's own; inactive and non-frontend engineers never are.
        // (The shared test database holds other tests' engineers too, so assert on these ones.)
        candidates.Single(c => c.Id == feMember.Id).OnProject.Should().BeTrue();
        candidates.Single(c => c.Id == feNotOnProject.Id).OnProject.Should().BeFalse();
        candidates.Should().OnlyContain(c => c.Discipline == "frontend");
        candidates.Should().NotContain(c => c.Id == feInactive.Id || c.Id == otherBe.Id);
        candidates.Select(c => c.OnProject).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Frontend_assignee_sees_the_backend_engineers_for_handing_back()
    {
        var fe = await SeedEngineerWithDisciplineAsync("hc_fe_assignee@pulse.io", Discipline.Frontend);
        var be = await SeedEngineerWithDisciplineAsync("hc_be_back@pulse.io", Discipline.Backend);
        var project = await SeedProjectAsync("Handoff Back Project");
        await SeedProjectMemberAsync(project.Id, fe.Id);
        await SeedProjectMemberAsync(project.Id, be.Id);
        var task = await SeedTaskAsync("Handed to FE", project.Id, assigneeId: fe.Id);

        var client = await AuthenticatedClientAsync("hc_fe_assignee@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id, "backend");

        candidates.Select(c => c.Id).Should().Contain(be.Id);
        candidates.Single(c => c.Id == be.Id).OnProject.Should().BeTrue();
    }

    [Fact]
    public async Task Candidates_include_the_owning_teams_roster_not_just_explicit_members()
    {
        var team = await SeedTeamAsync("Handoff Roster Team");
        var be = await SeedEngineerWithDisciplineAsync("hc_roster_be@pulse.io", Discipline.Backend);
        var fe = await SeedEngineerWithDisciplineAsync("hc_roster_fe@pulse.io", Discipline.Frontend);
        await AssignEngineerToTeamAsync(fe.Id, team.Id);
        var project = await SeedProjectAsync("Handoff Roster Project", team.Id);
        await SeedProjectMemberAsync(project.Id, be.Id);
        var task = await SeedTaskAsync("Roster task", project.Id, assigneeId: be.Id);

        var client = await AuthenticatedClientAsync("hc_roster_be@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id, "frontend");

        candidates.Single(c => c.Id == fe.Id).OnProject.Should().BeTrue();
    }

    [Fact]
    public async Task Handing_off_to_an_offered_engineer_who_is_not_on_the_project_gives_them_access()
    {
        var be = await SeedEngineerWithDisciplineAsync("hc_ho_be@pulse.io", Discipline.Backend);
        var fe = await SeedEngineerWithDisciplineAsync("hc_ho_fe@pulse.io", Discipline.Frontend);
        var project = await SeedProjectAsync("Handoff New Member Project");
        await SeedProjectMemberAsync(project.Id, be.Id);
        // The task has to be a two-stage one for the hand-off to be valid.
        var beClient = await AuthenticatedClientAsync("hc_ho_be@pulse.io");
        var created = await beClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Two-stage", points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id, assigneeId = be.Id, requiresFrontendHandoff = true,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var twoStage = (await created.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;

        var candidates = await GetCandidatesAsync(beClient, twoStage.Id, "frontend");
        candidates.Single(c => c.Id == fe.Id).OnProject.Should().BeFalse();

        var handOff = await beClient.PostAsJsonAsync(
            $"/api/v1/tasks/{twoStage.Id}/hand-off-to-frontend", new { frontendAssigneeId = fe.Id });
        handOff.StatusCode.Should().Be(HttpStatusCode.OK);

        var feClient = await AuthenticatedClientAsync("hc_ho_fe@pulse.io");
        (await feClient.GetAsync($"/api/v1/tasks/{twoStage.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pmo_still_sees_the_candidates()
    {
        await SeedEngineerAsync("hc_pmo@pulse.io", Roles.HeadOfPmo);
        var be = await SeedEngineerWithDisciplineAsync("hc_pmo_be@pulse.io", Discipline.Backend);
        var fe = await SeedEngineerWithDisciplineAsync("hc_pmo_fe@pulse.io", Discipline.Frontend);
        var project = await SeedProjectAsync("Handoff PMO Project");
        await SeedProjectMemberAsync(project.Id, fe.Id);
        var task = await SeedTaskAsync("PMO view", project.Id, assigneeId: be.Id);

        var client = await AuthenticatedClientAsync("hc_pmo@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id, "frontend");

        candidates.Select(c => c.Id).Should().Contain(fe.Id);
    }

    [Fact]
    public async Task Engineer_with_no_access_to_the_task_is_forbidden()
    {
        var owner = await SeedEngineerWithDisciplineAsync("hc_owner@pulse.io", Discipline.Backend);
        await SeedEngineerWithDisciplineAsync("hc_outsider@pulse.io", Discipline.Backend);
        var project = await SeedProjectAsync("Handoff Private Project");
        await SeedProjectMemberAsync(project.Id, owner.Id);
        var task = await SeedTaskAsync("Private", project.Id, assigneeId: owner.Id);

        var client = await AuthenticatedClientAsync("hc_outsider@pulse.io");
        var resp = await client.GetAsync($"/api/v1/tasks/{task.Id}/handoff-candidates?discipline=frontend");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("design")]
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task Only_frontend_and_backend_are_valid_disciplines(string discipline)
    {
        var be = await SeedEngineerWithDisciplineAsync($"hc_valid_{discipline}@pulse.io", Discipline.Backend);
        var project = await SeedProjectAsync($"Handoff Validation {discipline}");
        await SeedProjectMemberAsync(project.Id, be.Id);
        var task = await SeedTaskAsync("Validation", project.Id, assigneeId: be.Id);

        var client = await AuthenticatedClientAsync($"hc_valid_{discipline}@pulse.io");
        var resp = await client.GetAsync($"/api/v1/tasks/{task.Id}/handoff-candidates?discipline={discipline}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
