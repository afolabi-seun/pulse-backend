using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>
/// Regression guard for a bug found while investigating why a Sprint Detail board under-reported
/// its own task count: GET /tasks?sprintId=... never told ListTasksQuery's per-role narrowing
/// (an IC's "my tasks only" fallback, a lead/head's own team/department roster scoping) that the
/// caller had already been confirmed to have access to that *one specific* sprint — so both
/// narrowed the sprint's contents down to a subset instead of showing everything in it.
/// </summary>
[Collection("Integration")]
public class SprintTaskVisibilityTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public SprintTaskVisibilityTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Engineer_viewing_their_teams_sprint_sees_every_teammates_task_not_just_their_own()
    {
        var team = await SeedTeamAsync("Sprint Visibility Team");
        var viewer = await SeedEngineerAsync("sprintvis_viewer@pulse.io", Roles.Engineer);
        var teammate = await SeedEngineerAsync("sprintvis_teammate@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(viewer.Id, team.Id);
        await AssignEngineerToTeamAsync(teammate.Id, team.Id);
        var project = await SeedProjectAsync("Sprint Visibility Project", team.Id);
        var sprint = await SeedSprintAsync(team.Id, projectId: project.Id);

        var ownTask = await SeedTaskAsync("Viewer's own task", project.Id, assigneeId: viewer.Id);
        await AssignTaskToSprintAsync(ownTask.Id, sprint.Id);
        var teammateTask = await SeedTaskAsync("Teammate's task", project.Id, assigneeId: teammate.Id);
        await AssignTaskToSprintAsync(teammateTask.Id, sprint.Id);

        var client = await AuthenticatedClientAsync("sprintvis_viewer@pulse.io");
        var response = await client.GetAsync($"/api/v1/tasks?sprintId={sprint.Id}&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();
        titles.Should().Contain("Viewer's own task");
        titles.Should().Contain("Teammate's task");
    }

    [Fact]
    public async Task TeamLead_viewing_their_sprint_sees_a_task_assigned_outside_their_department()
    {
        var lead = await SeedEngineerAsync("sprintvis_lead@pulse.io", Roles.TeamLead);
        var leadTeam = await SeedTeamAsync("Sprint Visibility Lead Team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, leadTeam.Id);
        var project = await SeedProjectAsync("Sprint Visibility Lead Project", leadTeam.Id);
        var sprint = await SeedSprintAsync(leadTeam.Id, projectId: project.Id);

        // A reviewer from a completely different team — not on the lead's roster.
        var otherTeam = await SeedTeamAsync("Sprint Visibility Other Team");
        var reviewer = await SeedEngineerAsync("sprintvis_reviewer@pulse.io", Roles.Engineer, isQa: true);
        await AssignEngineerToTeamAsync(reviewer.Id, otherTeam.Id);

        var reviewTask = await SeedTaskAsync("[QA] Cross-team review", project.Id, assigneeId: reviewer.Id);
        await AssignTaskToSprintAsync(reviewTask.Id, sprint.Id);

        var client = await AuthenticatedClientAsync("sprintvis_lead@pulse.io");
        var response = await client.GetAsync($"/api/v1/tasks?sprintId={sprint.Id}&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        body!.Data!.Items.Select(t => t.Title).Should().Contain("[QA] Cross-team review");
    }

    [Fact]
    public async Task Engineer_with_no_connection_to_the_sprint_does_not_see_its_tasks()
    {
        var team = await SeedTeamAsync("Sprint Visibility Denied Team");
        var project = await SeedProjectAsync("Sprint Visibility Denied Project", team.Id);
        var sprint = await SeedSprintAsync(team.Id, projectId: project.Id);
        var teamMember = await SeedEngineerAsync("sprintvis_owner@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(teamMember.Id, team.Id);
        var task = await SeedTaskAsync("Denied sprint's task", project.Id, assigneeId: teamMember.Id);
        await AssignTaskToSprintAsync(task.Id, sprint.Id);

        await SeedEngineerAsync("sprintvis_outsider@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("sprintvis_outsider@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks?sprintId={sprint.Id}&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        body!.Data!.Items.Select(t => t.Title).Should().NotContain("Denied sprint's task");
    }
}
