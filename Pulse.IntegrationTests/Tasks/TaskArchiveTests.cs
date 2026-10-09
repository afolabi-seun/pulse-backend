using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Application.Tasks.Archive;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>
/// The pilot clean-up: tasks created before a baseline day are archived (hidden, not deleted) so the system starts fresh from it. Every call here is
/// scoped to the test's own project, because the integration database is shared and an unscoped archive would sweep other tests' tasks.
/// </summary>
[Collection("Integration")]
public class TaskArchiveTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskArchiveTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static readonly string Today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    /// <summary>Makes a project's tasks (and their history) look like they were created long before the baseline.</summary>
    private async Task BackdateAsync(Guid projectId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var past = DateTime.UtcNow.AddDays(-60);
        var ids = await db.Tasks.Where(t => t.ProjectId == projectId).Select(t => t.Id).ToListAsync();
        await db.Tasks.Where(t => t.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, past));
        await db.TaskHistory.Where(h => ids.Contains(h.TaskId)).ExecuteUpdateAsync(s => s.SetProperty(h => h.ChangedAt, past));
    }

    private static object Body(Guid projectId, bool dryRun, bool includeTouched = false, string? reason = "Pilot clean-up") =>
        new { baselineStart = Today, projectIds = new[] { projectId }, includeTouched, dryRun, reason };

    private async Task<ArchiveTasksResult> ArchiveAsync(HttpClient pmo, Guid projectId, bool dryRun, bool includeTouched = false)
    {
        var res = await pmo.PostAsJsonAsync("/api/v1/tasks/archive", Body(projectId, dryRun, includeTouched));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<ArchiveTasksResult>>(JsonOpts))!.Data!;
    }

    private async Task<int> VisibleCountAsync(Guid projectId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PulseDbContext>().Tasks.CountAsync(t => t.ProjectId == projectId);
    }

    [Fact]
    public async Task A_dry_run_reports_what_would_be_archived_and_changes_nothing()
    {
        var pmo = await SeedEngineerAsync("ar_pmo_dry@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive dry run");
        var a = await SeedTaskAsync("Old backlog", project.Id);
        await SeedTaskAsync("Old done", project.Id);
        await BackdateAsync(project.Id);
        await MarkTaskDoneAsync(a.Id);
        await BackdateAsync(project.Id);
        var client = await AuthenticatedClientAsync("ar_pmo_dry@pulse.io");

        var result = await ArchiveAsync(client, project.Id, dryRun: true);

        result.DryRun.Should().BeTrue();
        result.ToArchive.Should().Be(2);
        result.Done.Should().Be(1);
        result.Open.Should().Be(1);
        result.OpenTasks.Should().ContainSingle(t => t.Title == "Old done" || t.Title == "Old backlog");
        (await VisibleCountAsync(project.Id)).Should().Be(2, "a dry run must not change anything");
    }

    [Fact]
    public async Task Archiving_hides_old_tasks_keeps_newer_ones_and_lists_them_as_archived()
    {
        await SeedEngineerAsync("ar_pmo_run@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive run");
        var old = await SeedTaskAsync("Pilot leftover", project.Id);
        await BackdateAsync(project.Id);
        var fresh = await SeedTaskAsync("October work", project.Id);
        var client = await AuthenticatedClientAsync("ar_pmo_run@pulse.io");

        var result = await ArchiveAsync(client, project.Id, dryRun: false);

        result.ToArchive.Should().Be(1);
        (await VisibleCountAsync(project.Id)).Should().Be(1);
        (await client.GetAsync($"/api/v1/tasks/{fresh.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var gone = await client.GetAsync($"/api/v1/tasks/{old.Id}");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await gone.Content.ReadAsStringAsync()).Should().Contain("archived");

        var list = (await (await client.GetAsync($"/api/v1/tasks/archived?projectId={project.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<ArchivedTaskPage>>(JsonOpts))!.Data!;
        list.Total.Should().Be(1);
        list.Items.Single().Title.Should().Be("Pilot leftover");
        list.Items.Single().Reason.Should().Be("Pilot clean-up");
    }

    [Fact]
    public async Task A_task_with_activity_since_the_baseline_is_kept_unless_overridden()
    {
        var eng = await SeedEngineerAsync("ar_eng_touch@pulse.io");
        await SeedEngineerAsync("ar_pmo_touch@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive touched");
        var live = await SeedTaskAsync("Still being worked", project.Id);
        await SeedTaskAsync("Dormant", project.Id);
        await BackdateAsync(project.Id);
        await SeedCommentAsync(live.Id, eng.Id);
        var client = await AuthenticatedClientAsync("ar_pmo_touch@pulse.io");

        var preview = await ArchiveAsync(client, project.Id, dryRun: true);
        preview.ToArchive.Should().Be(1);
        preview.KeptBecauseTouched.Should().Be(1);
        preview.KeptTasks.Should().ContainSingle(t => t.Title == "Still being worked");

        (await ArchiveAsync(client, project.Id, dryRun: false)).ToArchive.Should().Be(1);
        (await VisibleCountAsync(project.Id)).Should().Be(1);

        (await ArchiveAsync(client, project.Id, dryRun: false, includeTouched: true)).ToArchive.Should().Be(1);
        (await VisibleCountAsync(project.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_QA_task_created_after_the_cutoff_is_archived_and_restored_with_its_parent()
    {
        await SeedEngineerAsync("ar_pmo_qa@pulse.io", Roles.HeadOfPmo);
        var engineer = await SeedEngineerAsync("ar_eng_qa@pulse.io");
        var project = await SeedProjectAsync("Archive QA");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var parent = await SeedTaskAsync("Needs review", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var engineerClient = await AuthenticatedClientAsync("ar_eng_qa@pulse.io");
        (await engineerClient.PostAsync($"/api/v1/tasks/{parent.Id}/send-to-qa", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        // Only the parent predates the baseline; its QA task was created "now".
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var past = DateTime.UtcNow.AddDays(-60);
            await db.Tasks.Where(t => t.Id == parent.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, past));
            await db.TaskHistory.Where(h => h.TaskId == parent.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.ChangedAt, past));
        }
        var pmo = await AuthenticatedClientAsync("ar_pmo_qa@pulse.io");

        // Creating the QA task is not activity on the pair, so the old parent takes its newer QA task with it.
        (await ArchiveAsync(pmo, project.Id, dryRun: false)).ToArchive.Should().Be(2);
        (await VisibleCountAsync(project.Id)).Should().Be(0);

        var restore = await pmo.PostAsync($"/api/v1/tasks/{parent.Id}/restore", null);
        restore.StatusCode.Should().Be(HttpStatusCode.OK);
        (await restore.Content.ReadFromJsonAsync<ApiResponse<int>>(JsonOpts))!.Data.Should().Be(2);
        (await VisibleCountAsync(project.Id)).Should().Be(2);
        (await pmo.GetAsync($"/api/v1/tasks/{parent.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task New_tasks_keep_numbering_after_older_ones_are_archived()
    {
        var pmoEng = await SeedEngineerAsync("ar_pmo_num@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive numbering");
        await SeedProjectMemberAsync(project.Id, pmoEng.Id);
        var old = await SeedTaskAsync("Old one", project.Id);
        await BackdateAsync(project.Id);
        var pmo = await AuthenticatedClientAsync("ar_pmo_num@pulse.io");
        await ArchiveAsync(pmo, project.Id, dryRun: false);

        var created = await pmo.PostAsJsonAsync("/api/v1/tasks", new { title = "First of October", points = 2, projectId = project.Id, dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)) });

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var dto = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        dto.TaskNumber.Should().BeGreaterThan(old.TaskNumber, "reusing an archived task's number would collide with it and confuse every reference to it");
    }

    [Fact]
    public async Task Only_PMO_can_archive_list_or_restore()
    {
        await SeedEngineerAsync("ar_eng_deny@pulse.io");
        var project = await SeedProjectAsync("Archive denied");
        var task = await SeedTaskAsync("Anything", project.Id);
        var engineer = await AuthenticatedClientAsync("ar_eng_deny@pulse.io");

        (await engineer.PostAsJsonAsync("/api/v1/tasks/archive", Body(project.Id, dryRun: true))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await engineer.GetAsync("/api/v1/tasks/archived")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await engineer.PostAsync($"/api/v1/tasks/{task.Id}/restore", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Archiving_for_real_requires_a_reason()
    {
        await SeedEngineerAsync("ar_pmo_reason@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive reason");
        var pmo = await AuthenticatedClientAsync("ar_pmo_reason@pulse.io");

        var res = await pmo.PostAsJsonAsync("/api/v1/tasks/archive", Body(project.Id, dryRun: false, reason: " "));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_impossible_UTC_offset_is_rejected()
    {
        await SeedEngineerAsync("ar_pmo_offset@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Archive offset");
        var pmo = await AuthenticatedClientAsync("ar_pmo_offset@pulse.io");

        var res = await pmo.PostAsJsonAsync("/api/v1/tasks/archive",
            new { baselineStart = Today, projectIds = new[] { project.Id }, dryRun = true, utcOffsetMinutes = 24 * 60 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
