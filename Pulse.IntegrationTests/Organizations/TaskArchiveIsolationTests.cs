using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks.Archive;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// Archiving is a whole-system action, so in a multi-tenant deployment "whole system" must still mean "my organization": one organization's PMO
/// archiving, listing or restoring must never reach another organization's tasks. Archived tasks are shown through a context scope rather than
/// IgnoreQueryFilters precisely so the organization filter stays on; these tests pin that.
/// </summary>
[Collection("Integration")]
public class TaskArchiveIsolationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TaskArchiveIsolationTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static readonly string Today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private sealed record Orgs(HttpClient A, HttpClient B, Guid ProjectA, Guid TaskA, Guid ProjectB, Guid TaskB);

    private async Task<Orgs> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Guid orgBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var orgB = Organization.Create("Archive org B", $"arch-b-{suffix}");
            db.Organizations.Add(orgB);
            await db.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        await SeedEngineerAsync($"arch_a_{suffix}@pulse.io", Roles.HeadOfPmo);
        var pmoB = await SeedEngineerAsync($"arch_b_{suffix}@pulse.io", Roles.HeadOfPmo);
        await MoveAsync(pmoB, orgBId);

        var projectA = await SeedProjectAsync($"Archive A {suffix}");
        var projectB = await SeedProjectAsync($"Archive B {suffix}");
        await MoveAsync(projectB, orgBId);
        var taskA = await SeedTaskAsync($"Org A task {suffix}", projectA.Id);
        var taskB = await SeedTaskAsync($"Org B task {suffix}", projectB.Id);
        await BackdateAsync(taskA.Id, taskB.Id);

        return new Orgs(await AuthenticatedClientAsync($"arch_a_{suffix}@pulse.io"), await AuthenticatedClientAsync($"arch_b_{suffix}@pulse.io"),
            projectA.Id, taskA.Id, projectB.Id, taskB.Id);
    }

    private async Task MoveAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    private async Task BackdateAsync(params Guid[] taskIds)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var past = DateTime.UtcNow.AddDays(-60);
        await db.Tasks.Where(t => taskIds.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, past));
        await db.TaskHistory.Where(h => taskIds.Contains(h.TaskId)).ExecuteUpdateAsync(s => s.SetProperty(h => h.ChangedAt, past));
    }

    /// <summary>Unauthenticated context (unfiltered by organization), looking at archived rows too.</summary>
    private async Task<bool> IsArchivedAsync(Guid taskId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        using var _ = db.IncludeArchivedTasks();
        return await db.Tasks.Where(t => t.Id == taskId).Select(t => t.ArchivedAt != null).SingleAsync();
    }

    private static object Body(bool dryRun, Guid[]? projectIds = null) =>
        new { baselineStart = Today, projectIds, includeTouched = true, dryRun, reason = "Pilot clean-up" };

    [Fact]
    public async Task Archiving_the_whole_system_only_reaches_the_callers_own_organization()
    {
        var o = await SeedAsync();

        var res = await o.A.PostAsJsonAsync("/api/v1/tasks/archive", Body(dryRun: false));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await res.Content.ReadFromJsonAsync<ApiResponse<ArchiveTasksResult>>(JsonOpts))!.Data!;
        result.Projects.Should().NotContain(p => p.ProjectId == o.ProjectB, "org B's projects are not part of org A's system");
        (await IsArchivedAsync(o.TaskA)).Should().BeTrue();
        (await IsArchivedAsync(o.TaskB)).Should().BeFalse("org A's archive must not touch org B's tasks");
    }

    [Fact]
    public async Task Naming_another_organizations_project_archives_nothing()
    {
        var o = await SeedAsync();

        var res = await o.A.PostAsJsonAsync("/api/v1/tasks/archive", Body(dryRun: false, [o.ProjectB]));

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<ApiResponse<ArchiveTasksResult>>(JsonOpts))!.Data!.ToArchive.Should().Be(0);
        (await IsArchivedAsync(o.TaskB)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organization_cannot_list_restore_or_hint_at_archived_tasks()
    {
        var o = await SeedAsync();
        (await o.A.PostAsJsonAsync("/api/v1/tasks/archive", Body(dryRun: false, [o.ProjectA]))).StatusCode.Should().Be(HttpStatusCode.OK);

        var listA = await (await o.A.GetAsync("/api/v1/tasks/archived")).Content.ReadFromJsonAsync<ApiResponse<ArchivedTaskPage>>(JsonOpts);
        listA!.Data!.Items.Should().Contain(t => t.Id == o.TaskA, "control: org A sees its own archived task");

        var listB = await (await o.B.GetAsync("/api/v1/tasks/archived")).Content.ReadFromJsonAsync<ApiResponse<ArchivedTaskPage>>(JsonOpts);
        listB!.Data!.Items.Should().NotContain(t => t.Id == o.TaskA);

        (await o.B.PostAsync($"/api/v1/tasks/{o.TaskA}/restore", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await IsArchivedAsync(o.TaskA)).Should().BeTrue("org B's restore must not have worked");

        var open = await o.B.GetAsync($"/api/v1/tasks/{o.TaskA}");
        open.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await open.Content.ReadAsStringAsync()).Should().NotContain("archived", "org B must not even learn the task exists");
    }
}
