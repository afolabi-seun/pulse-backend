using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Search;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Covers the two spots where a task's display key (TaskKey, e.g. "NOTIF-011") is resolved
/// outside TaskDto itself — global search and task dependency links — added as part of rolling the
/// key out to every place a task is referenced by title alone.</summary>
[Collection("Integration")]
public class TaskKeyEverywhereTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskKeyEverywhereTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Search_includes_the_tasks_display_key()
    {
        await SeedEngineerAsync("taskkey_search_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("TaskKey search project", code: "TKSEARCH");
        var task = await SeedTaskAsync("Findable via search TaskKey", project.Id);
        var client = await AuthenticatedClientAsync("taskkey_search_pm@pulse.io");

        var resp = await client.GetAsync("/api/v1/search?q=Findable via search TaskKey");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<SearchResultDto>>(JsonOpts);

        var hit = body!.Data!.Tasks.Should().ContainSingle(t => t.Id == task.Id).Subject;
        hit.TaskKey.Should().Be($"TKSEARCH-{task.TaskNumber}");
    }

    [Fact]
    public async Task GetDependencies_includes_the_linked_tasks_display_key()
    {
        await SeedEngineerAsync("taskkey_deps_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("TaskKey deps project", code: "TKDEPS");
        var blocker = await SeedTaskAsync("Blocking task", project.Id);
        var dependent = await SeedTaskAsync("Dependent task", project.Id);
        var client = await AuthenticatedClientAsync("taskkey_deps_pm@pulse.io");

        (await client.PostAsJsonAsync($"/api/v1/tasks/{dependent.Id}/dependencies",
            new { blockingTaskId = blocker.Id })).EnsureSuccessStatusCode();

        var resp = await client.GetAsync($"/api/v1/tasks/{dependent.Id}/dependencies");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<TaskLinksDto>>(JsonOpts);

        var blockedBy = body!.Data!.BlockedBy.Should().ContainSingle().Subject;
        blockedBy.TaskKey.Should().Be($"TKDEPS-{blocker.TaskNumber}");
    }
}
