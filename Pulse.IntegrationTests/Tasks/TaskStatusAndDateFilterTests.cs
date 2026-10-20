using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>The task list's `statuses` (several at once) and `dateFrom`/`dateTo` filters, which back the engineer page's Current and History tabs.</summary>
[Collection("Integration")]
public class TaskStatusAndDateFilterTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskStatusAndDateFilterTests(PulseWebApplicationFactory factory) : base(factory) { }

    private sealed record Seeded(Guid ProjectId, Guid Active, Guid Paused, Guid DoneRecent, Guid DoneOld, Guid BacklogUndated, Guid BacklogDueOld);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<(Seeded Tasks, HttpClient Client)> SeedMixedAsync(string tag)
    {
        var pmEmail = $"{tag}_pm@pulse.io";
        await SeedEngineerAsync(pmEmail, Roles.ProjectManager);
        var engineer = await SeedEngineerAsync($"{tag}_eng@pulse.io");
        var project = await SeedProjectAsync($"{tag} project");

        var active = await SeedTaskAsync("Active one", project.Id, assigneeId: engineer.Id);
        var paused = await SeedTaskAsync("Paused one", project.Id, assigneeId: engineer.Id);
        var doneRecent = await SeedTaskAsync("Done recently", project.Id, assigneeId: engineer.Id);
        var doneOld = await SeedTaskAsync("Done long ago", project.Id, assigneeId: engineer.Id);
        var backlogUndated = await SeedTaskAsync("Backlog without a date", project.Id);
        var backlogDueOld = await SeedTaskAsync("Backlog due long ago", project.Id, dueDaysFromNow: -90);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var byId = await db.Tasks.Where(t => t.ProjectId == project.Id).ToDictionaryAsync(t => t.Id);
            byId[paused.Id].Pause("waiting", engineer.Id);
            byId[doneRecent.Id].MarkDone(engineer.Id);
            byId[doneRecent.Id].SetActualEndDate(Today.AddDays(-5));
            byId[doneOld.Id].MarkDone(engineer.Id);
            byId[doneOld.Id].SetActualEndDate(Today.AddDays(-60));
            var undated = byId[backlogUndated.Id];
            undated.UpdateDetails(undated.Title, null, null, undated.Points, null, engineer.Id);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync(pmEmail);
        return (new Seeded(project.Id, active.Id, paused.Id, doneRecent.Id, doneOld.Id, backlogUndated.Id, backlogDueOld.Id), client);
    }

    private async Task<List<Guid>> ListIdsAsync(HttpClient client, Guid projectId, string query)
    {
        var resp = await client.GetAsync($"/api/v1/tasks?projectId={projectId}&limit=100&{query}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        return body!.Data!.Items.Select(t => t.Id).ToList();
    }

    [Fact]
    public async Task Statuses_keeps_only_the_listed_statuses()
    {
        var (t, client) = await SeedMixedAsync("statuses");

        var ids = await ListIdsAsync(client, t.ProjectId, "statuses=active,paused");

        ids.Should().BeEquivalentTo([t.Active, t.Paused]);
    }

    [Fact]
    public async Task Statuses_with_an_unknown_value_is_a_validation_error()
    {
        var (t, client) = await SeedMixedAsync("badstatus");

        var resp = await client.GetAsync($"/api/v1/tasks?projectId={t.ProjectId}&statuses=active,nonsense");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DateFrom_reads_done_tasks_by_completion_date_and_the_rest_by_due_date()
    {
        var (t, client) = await SeedMixedAsync("datefrom");

        var ids = await ListIdsAsync(client, t.ProjectId, $"dateFrom={Today.AddDays(-30):yyyy-MM-dd}");

        // The recent Done task and everything still due later stay; the Done task finished 60 days ago and the Backlog task due 90 days ago fall out.
        ids.Should().Contain([t.Active, t.Paused, t.DoneRecent]);
        ids.Should().NotContain([t.DoneOld, t.BacklogDueOld]);
    }

    [Fact]
    public async Task Undated_unfinished_work_is_never_dropped_by_the_date_range()
    {
        var (t, client) = await SeedMixedAsync("undated");

        var ids = await ListIdsAsync(client, t.ProjectId, $"dateFrom={Today.AddDays(-1):yyyy-MM-dd}&dateTo={Today.AddDays(1):yyyy-MM-dd}");

        ids.Should().Contain(t.BacklogUndated);
        ids.Should().NotContain([t.DoneRecent, t.DoneOld, t.Active]);
    }

    [Fact]
    public async Task DateTo_excludes_done_tasks_completed_after_it()
    {
        var (t, client) = await SeedMixedAsync("dateto");

        var ids = await ListIdsAsync(client, t.ProjectId, $"statuses=done&dateTo={Today.AddDays(-30):yyyy-MM-dd}");

        ids.Should().BeEquivalentTo([t.DoneOld]);
    }

    [Fact]
    public async Task DateFrom_after_dateTo_is_a_validation_error()
    {
        var (t, client) = await SeedMixedAsync("reversed");

        var resp = await client.GetAsync($"/api/v1/tasks?projectId={t.ProjectId}&dateFrom=2026-10-10&dateTo=2026-10-01");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Statuses_and_a_date_range_combine_and_still_paginate()
    {
        var (t, client) = await SeedMixedAsync("combined");

        var first = await client.GetFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(
            $"/api/v1/tasks?projectId={t.ProjectId}&statuses=done,paused,backlog&dateFrom={Today.AddDays(-30):yyyy-MM-dd}&sortBy=dueDate&sortDirection=desc&limit=1", JsonOpts);
        first!.Data!.Items.Should().HaveCount(1);
        first.Data.NextCursor.Should().NotBeNull();

        var all = await ListIdsAsync(client, t.ProjectId, $"statuses=done,paused,backlog&dateFrom={Today.AddDays(-30):yyyy-MM-dd}");
        all.Should().BeEquivalentTo([t.Paused, t.DoneRecent, t.BacklogUndated]);
    }

    private static HttpContent BaselineBody(DateOnly? d) => JsonContent.Create(new { date = d?.ToString("yyyy-MM-dd") });

    [Fact]
    public async Task Finished_tasks_from_before_the_reporting_start_date_are_hidden_from_lists_unless_asked_for()
    {
        var (t, client) = await SeedMixedAsync("hidefinished");
        var pmoEmail = "hidefinished_pmo@pulse.io";
        await SeedEngineerAsync(pmoEmail, Roles.HeadOfPmo);
        var pmo = await AuthenticatedClientAsync(pmoEmail);
        try
        {
            (await pmo.PutAsync("/api/v1/reports/baseline", BaselineBody(Today.AddDays(-30)))).StatusCode.Should().Be(HttpStatusCode.OK);

            var ids = await ListIdsAsync(client, t.ProjectId, "");

            ids.Should().NotContain(t.DoneOld, "it was finished 60 days ago, before the start date");
            ids.Should().Contain([t.DoneRecent, t.Active, t.Paused, t.BacklogUndated]);
            ids.Should().Contain(t.BacklogDueOld, "unfinished work is never hidden, however old its due date");

            var everything = await ListIdsAsync(client, t.ProjectId, "includeBeforeReportingStart=true");
            everything.Should().Contain(t.DoneOld);
        }
        finally
        {
            await pmo.PutAsync("/api/v1/reports/baseline", BaselineBody(null)); // leave the shared server clean for other tests
        }
    }

    [Fact]
    public async Task A_single_sprint_keeps_every_task_even_before_the_reporting_start_date()
    {
        var (t, client) = await SeedMixedAsync("hidesprint");
        var team = await SeedTeamAsync("Hide sprint team");
        var sprint = await SeedSprintAsync(team.Id, "Hide sprint", t.ProjectId);
        await AssignTaskToSprintAsync(t.DoneOld, sprint.Id);
        var pmoEmail = "hidesprint_pmo@pulse.io";
        await SeedEngineerAsync(pmoEmail, Roles.HeadOfPmo);
        var pmo = await AuthenticatedClientAsync(pmoEmail);
        try
        {
            (await pmo.PutAsync("/api/v1/reports/baseline", BaselineBody(Today.AddDays(-30)))).StatusCode.Should().Be(HttpStatusCode.OK);

            var resp = await client.GetAsync($"/api/v1/tasks?sprintId={sprint.Id}&limit=100");
            var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);

            body!.Data!.Items.Select(x => x.Id).Should().Contain(t.DoneOld);
        }
        finally
        {
            await pmo.PutAsync("/api/v1/reports/baseline", BaselineBody(null));
        }
    }
}
