using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Sprints;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Changing an existing due date requires a stated reason, which then shows up in the
/// project Activity feed and on the task detail.</summary>
[Collection("Integration")]
public class DueDateChangeReasonTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public DueDateChangeReasonTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string Day(int fromNow) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(fromNow)).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Team_lead_changing_an_existing_due_date_without_a_reason_is_rejected()
    {
        var lead = await SeedEngineerAsync("ddr_lead_noreason@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("DDR No Reason");
        var task = await SeedTaskAsync("Needs reason", project.Id, dueDaysFromNow: 5, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("ddr_lead_noreason@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { dueDate = Day(9) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await (await client.GetAsync($"/api/v1/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.DueDate.Should().Be(DateOnly.Parse(Day(5)));
    }

    [Fact]
    public async Task Reason_is_recorded_and_shown_in_activity_feed_and_on_the_task()
    {
        var lead = await SeedEngineerAsync("ddr_lead_reason@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("DDR With Reason");
        var task = await SeedTaskAsync("Moves once", project.Id, dueDaysFromNow: 5, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("ddr_lead_reason@pulse.io");

        var patch = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}",
            new { dueDate = Day(9), dueDateChangeReason = "  Vendor delayed the API  " });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var activity = await (await client.GetAsync($"/api/v1/projects/{project.Id}/activity"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts);
        activity!.Data!.Items.Should().Contain(e => e.TaskId == task.Id
            && e.Summary.Contains("reason: Vendor delayed the API")
            && e.Summary.StartsWith("changed due date from"));

        var detail = await (await client.GetAsync($"/api/v1/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        var change = detail!.Data!.DueDateChange;
        change.Should().NotBeNull();
        change!.Reason.Should().Be("Vendor delayed the API");
        change.From.Should().Be(DateOnly.Parse(Day(5)));
        change.To.Should().Be(DateOnly.Parse(Day(9)));
        change.ChangedById.Should().Be(lead.Id);
        change.ChangedByName.Should().Be(lead.Name);
    }

    [Fact]
    public async Task Assignee_must_give_a_reason_to_move_their_own_due_date()
    {
        var engineer = await SeedEngineerAsync("ddr_assignee@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("DDR Assignee");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Mine", project.Id, dueDaysFromNow: 5, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("ddr_assignee@pulse.io");

        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/assignee-edit", new { dueDate = Day(8) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/assignee-edit",
            new { dueDate = Day(8), dueDateChangeReason = "Blocked on design" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sprint_end_date_change_that_shifts_tasks_requires_a_reason_recorded_on_each_task()
    {
        await SeedEngineerAsync("ddr_sprint_head@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("DDR Sprint Team");
        var project = await SeedProjectAsync("DDR Sprint", team.Id);
        var client = await AuthenticatedClientAsync("ddr_sprint_head@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints", new
        {
            projectId = project.Id, name = "DDR sprint", goal = "g",
            startDate = Day(0), endDate = Day(14),
        })).Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var task = await SeedTaskAsync("Shifts with sprint", project.Id, dueDaysFromNow: 10);
        await AssignTaskToSprintAsync(task.Id, created.Id);

        (await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { endDate = Day(20) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}",
            new { endDate = Day(20), dueDateChangeReason = "Release moved a week" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await (await client.GetAsync($"/api/v1/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        detail!.Data!.DueDateChange!.Reason.Should().Be("Release moved a week");
    }

    [Fact]
    public async Task Sprint_end_date_change_with_no_tasks_to_shift_needs_no_reason()
    {
        await SeedEngineerAsync("ddr_sprint_empty@pulse.io", Roles.HeadOfPmo);
        var team = await SeedTeamAsync("DDR Empty Sprint Team");
        var project = await SeedProjectAsync("DDR Empty Sprint", team.Id);
        var client = await AuthenticatedClientAsync("ddr_sprint_empty@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints", new
        {
            projectId = project.Id, name = "Empty sprint", goal = "g",
            startDate = Day(0), endDate = Day(14),
        })).Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        (await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { endDate = Day(21) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
