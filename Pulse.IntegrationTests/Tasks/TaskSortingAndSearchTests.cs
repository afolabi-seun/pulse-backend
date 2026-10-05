using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Search;
using Pulse.Application.Tasks;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Covers the explicit column-sort option on the task list and the task-key ("CODE-N")
/// matching added to both the Tasks list search box and global Search.</summary>
[Collection("Integration")]
public class TaskSortingAndSearchTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskSortingAndSearchTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task ListTasks_sortBy_points_desc_orders_every_task_by_points_regardless_of_status()
    {
        await SeedEngineerAsync("sort_points_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Sort points project");
        var low = await SeedTaskAsync("Low points", project.Id, points: 1);
        var high = await SeedTaskAsync("High points", project.Id, points: 8);
        var mid = await SeedTaskAsync("Mid points", project.Id, points: 3);
        var client = await AuthenticatedClientAsync("sort_points_pm@pulse.io");

        var resp = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}&sortBy=points&sortDirection=desc&limit=100");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);

        var ids = body!.Data!.Items.Select(t => t.Id).ToList();
        ids.IndexOf(high.Id).Should().BeLessThan(ids.IndexOf(mid.Id));
        ids.IndexOf(mid.Id).Should().BeLessThan(ids.IndexOf(low.Id));
    }

    [Fact]
    public async Task ListTasks_sortBy_pagination_does_not_skip_or_duplicate_rows()
    {
        await SeedEngineerAsync("sort_page_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Sort pagination project");
        var seeded = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var t = await SeedTaskAsync($"Sortable task {i}", project.Id, points: i + 1);
            seeded.Add(t.Id);
        }
        var client = await AuthenticatedClientAsync("sort_page_pm@pulse.io");

        var fullResp = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}&sortBy=points&sortDirection=asc&limit=100");
        var fullBody = await fullResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var fullOrder = fullBody!.Data!.Items.Select(t => t.Id).ToList();

        var paged = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/tasks?projectId={project.Id}&sortBy=points&sortDirection=asc&limit=2"
                + (cursor is not null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            var pageResp = await client.GetAsync(url);
            var pageBody = await pageResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
            paged.AddRange(pageBody!.Data!.Items.Select(t => t.Id));
            cursor = pageBody.Data.NextCursor;
        } while (cursor is not null);

        paged.Should().Equal(fullOrder);
    }

    [Fact]
    public async Task ListTasks_rejects_an_unsupported_sort_column()
    {
        await SeedEngineerAsync("sort_invalid_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("sort_invalid_pm@pulse.io");

        var resp = await client.GetAsync("/api/v1/tasks?sortBy=assignee");

        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Status.Should().Be("error");
    }

    [Fact]
    public async Task ListTasks_title_filter_matches_by_task_key()
    {
        await SeedEngineerAsync("key_list_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Key list project", code: "KEYLIST");
        var task = await SeedTaskAsync("Totally unrelated title", project.Id);
        var client = await AuthenticatedClientAsync("key_list_pm@pulse.io");

        var resp = await client.GetAsync($"/api/v1/tasks?title=KEYLIST-{task.TaskNumber}");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);

        body!.Data!.Items.Should().ContainSingle(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Search_matches_by_task_key()
    {
        await SeedEngineerAsync("key_search_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Key search project", code: "KEYSEARCH");
        var task = await SeedTaskAsync("Another unrelated title", project.Id);
        var client = await AuthenticatedClientAsync("key_search_pm@pulse.io");

        var resp = await client.GetAsync($"/api/v1/search?q=KEYSEARCH-{task.TaskNumber}");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<SearchResultDto>>(JsonOpts);

        body!.Data!.Tasks.Should().ContainSingle(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Search_matches_a_bare_task_number_against_any_project()
    {
        await SeedEngineerAsync("bare_number_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Bare number project", code: "BARENUM");
        // Search requires a query of at least 2 characters, so seed up to a double-digit task
        // number — a single-digit bare number (e.g. "1") would short-circuit before ever reaching
        // the task-key matching logic this test exercises.
        for (var i = 0; i < 9; i++)
            await SeedTaskAsync($"Filler task {i}", project.Id);
        var task = await SeedTaskAsync("Yet another unrelated title", project.Id);
        task.TaskNumber.Should().BeGreaterThanOrEqualTo(10);
        var client = await AuthenticatedClientAsync("bare_number_pm@pulse.io");

        var resp = await client.GetAsync($"/api/v1/search?q={task.TaskNumber}");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<SearchResultDto>>(JsonOpts);

        body!.Data!.Tasks.Should().Contain(t => t.Id == task.Id);
    }
}
