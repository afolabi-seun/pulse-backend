using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Projects;

[Collection("Integration")]
public class ProjectActivityTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ProjectActivityTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task GetProjectActivity_returns_recent_task_history_newest_first()
    {
        var engineer = await SeedEngineerAsync("activity_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Activity Project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Activity Task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("activity_eng@pulse.io");

        var doneResp = await client.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null);
        doneResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/activity");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts);
        var entries = body!.Data!.Items;
        entries.Should().HaveCountGreaterOrEqualTo(2);
        entries.Should().BeInDescendingOrder(e => e.ChangedAt);
        entries.Should().Contain(e => e.TaskId == task.Id && e.Summary.Contains("Done"));
        entries.Should().Contain(e => e.TaskId == task.Id && e.ActorName == engineer.Name);
    }

    [Fact]
    public async Task GetProjectActivity_returns_403_for_engineer_without_project_access()
    {
        await SeedEngineerAsync("activity_denied@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Activity Denied Project");
        var client = await AuthenticatedClientAsync("activity_denied@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{project.Id}/activity");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetProjectActivity_returns_404_for_unknown_project()
    {
        await SeedEngineerAsync("activity_404@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("activity_404@pulse.io");

        var response = await client.GetAsync($"/api/v1/projects/{Guid.NewGuid()}/activity");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
