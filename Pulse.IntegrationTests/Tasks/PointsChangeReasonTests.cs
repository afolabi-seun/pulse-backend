using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Changing the estimate of a task that has been started needs a stated reason, which then shows up in
/// the project Activity feed and on the task detail. Points feed velocity, workload and the overwork thresholds.</summary>
[Collection("Integration")]
public class PointsChangeReasonTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PointsChangeReasonTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Changing_the_points_of_a_started_task_without_a_reason_is_rejected()
    {
        var lead = await SeedEngineerAsync("pcr_lead_noreason@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PCR No Reason");
        var task = await SeedTaskAsync("Started task", project.Id, points: 3, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("pcr_lead_noreason@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { points = 8 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await (await client.GetAsync($"/api/v1/tasks/{task.Id}")).Content
            .ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Points.Should().Be(3);
        body.Data.Status.Should().Be("active");
    }

    [Fact]
    public async Task The_reason_is_recorded_and_shown_in_the_activity_feed_and_on_the_task()
    {
        var lead = await SeedEngineerAsync("pcr_lead_reason@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PCR With Reason");
        var task = await SeedTaskAsync("Re-estimated once", project.Id, points: 3, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("pcr_lead_reason@pulse.io");

        var patch = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}",
            new { points = 8, pointsChangeReason = "  The spike showed it is bigger  " });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var activity = await (await client.GetAsync($"/api/v1/projects/{project.Id}/activity"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts);
        activity!.Data!.Items.Should().Contain(e => e.TaskId == task.Id
            && e.Summary == "changed points from 3 to 8 — reason: The spike showed it is bigger");

        var detail = await (await client.GetAsync($"/api/v1/tasks/{task.Id}")).Content
            .ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        var change = detail!.Data!.PointsChange;
        change.Should().NotBeNull();
        change!.From.Should().Be(3);
        change.To.Should().Be(8);
        change.Reason.Should().Be("The spike showed it is bigger");
        change.ChangedById.Should().Be(lead.Id);
        change.ChangedByName.Should().Be(lead.Name);
    }

    [Fact]
    public async Task A_task_still_in_backlog_can_be_re_pointed_without_a_reason()
    {
        await SeedEngineerAsync("pcr_lead_backlog@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PCR Backlog");
        var task = await SeedTaskAsync("Not started", project.Id, points: 3);   // no assignee: Backlog
        var client = await AuthenticatedClientAsync("pcr_lead_backlog@pulse.io");

        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { points = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Leaving_the_points_alone_while_editing_something_else_needs_no_reason()
    {
        var lead = await SeedEngineerAsync("pcr_lead_other@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PCR Other Edit");
        var task = await SeedTaskAsync("Retitled", project.Id, points: 3, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("pcr_lead_other@pulse.io");

        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { title = "Retitled again" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { points = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_points_reason_over_500_characters_is_rejected()
    {
        var lead = await SeedEngineerAsync("pcr_lead_long@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PCR Long");
        var task = await SeedTaskAsync("Long reason", project.Id, points: 3, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("pcr_lead_long@pulse.io");

        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}",
            new { points = 8, pointsChangeReason = new string('x', 501) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
