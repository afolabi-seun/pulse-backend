using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Epics;
using Pulse.Application.Projects;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Projects;

/// <summary>
/// Regression guard for a gap found while auditing the timer feature: GET /projects/mine
/// (ListMyProjectsAsync) lists a project via three legs — explicit membership, owning team, or
/// merely having a task assigned there — but IProjectAccessPolicy.CanAccessProjectAsync only
/// implemented the first two. Fixed at the policy level so every caller (GetProjectQuery,
/// CreateTaskCommand, ListEpicsQuery, ...) picks it up at once rather than patching each one.
/// </summary>
[Collection("Integration")]
public class ProjectAccessGapTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ProjectAccessGapTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task GetProject_succeeds_for_an_engineer_connected_only_via_an_assigned_task()
    {
        var ownerTeam = await SeedTeamAsync("Access Gap Owner Team");
        var otherTeam = await SeedTeamAsync("Access Gap Engineer Team");
        var engineer = await SeedEngineerAsync("gap_get_project@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, otherTeam.Id);
        var client = await AuthenticatedClientAsync("gap_get_project@pulse.io");
        var project = await SeedProjectAsync("Access Gap GetProject", ownerTeamId: ownerTeam.Id);
        // Not a member, not on the owning team — connected only via this assigned task.
        await SeedTaskAsync("Assigned elsewhere", project.Id, points: 2, assigneeId: engineer.Id);

        var resp = await client.GetAsync($"/api/v1/projects/{project.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<ProjectDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(project.Id);
    }

    [Fact]
    public async Task CreateTask_succeeds_for_an_engineer_connected_only_via_an_assigned_task()
    {
        var ownerTeam = await SeedTeamAsync("Access Gap Owner Team 2");
        var otherTeam = await SeedTeamAsync("Access Gap Engineer Team 2");
        var engineer = await SeedEngineerAsync("gap_create_task@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, otherTeam.Id);
        var client = await AuthenticatedClientAsync("gap_create_task@pulse.io");
        var project = await SeedProjectAsync("Access Gap CreateTask", ownerTeamId: ownerTeam.Id);
        await SeedTaskAsync("Assigned elsewhere", project.Id, points: 2, assigneeId: engineer.Id);

        var resp = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "A second task", points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.ProjectId.Should().Be(project.Id);
    }

    [Fact]
    public async Task ListEpics_succeeds_for_an_engineer_connected_only_via_an_assigned_task()
    {
        var ownerTeam = await SeedTeamAsync("Access Gap Owner Team 3");
        var otherTeam = await SeedTeamAsync("Access Gap Engineer Team 3");
        var engineer = await SeedEngineerAsync("gap_list_epics@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, otherTeam.Id);
        var client = await AuthenticatedClientAsync("gap_list_epics@pulse.io");
        var project = await SeedProjectAsync("Access Gap ListEpics", ownerTeamId: ownerTeam.Id);
        await SeedTaskAsync("Assigned elsewhere", project.Id, points: 2, assigneeId: engineer.Id);

        var resp = await client.GetAsync($"/api/v1/epics?projectId={project.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await resp.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<EpicDto>>>(JsonOpts);
    }

    [Fact]
    public async Task An_engineer_with_no_connection_to_the_project_at_all_is_still_forbidden()
    {
        // Regression guard the other direction: the new leg must not become a blanket bypass.
        await SeedEngineerAsync("gap_truly_unrelated@pulse.io");
        var client = await AuthenticatedClientAsync("gap_truly_unrelated@pulse.io");
        var otherTeam = await SeedTeamAsync("Access Gap Unrelated Owner Team");
        var project = await SeedProjectAsync("Access Gap Unrelated Project", ownerTeamId: otherTeam.Id);

        var resp = await client.GetAsync($"/api/v1/projects/{project.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
