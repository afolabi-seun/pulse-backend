using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Reports;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Reports;

/// <summary>The PMO report shows work that sits outside an engineer's workload — paused tasks and assigned Backlog tasks with no estimate — beside the counts that do.</summary>
[Collection("Integration")]
public class UtilizationNotCountedTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public UtilizationNotCountedTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task The_PMO_report_counts_paused_and_unestimated_tasks_separately_from_the_workload()
    {
        var team = await SeedTeamAsync("Not counted team", department: "Not counted dept");
        var engineer = await SeedEngineerAsync("notcounted_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        await SeedEngineerAsync("notcounted_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Not counted project", team.Id);
        await SeedTaskAsync("Live work", project.Id, points: 5, assigneeId: engineer.Id);
        var held = await SeedTaskAsync("On hold", project.Id, points: 3, assigneeId: engineer.Id);
        await SeedTaskAsync("Never sized", project.Id, points: 0, assigneeId: engineer.Id);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.Tasks.SingleAsync(t => t.Id == held.Id)).Pause("waiting", engineer.Id);
            await db.SaveChangesAsync();
        }
        var pmo = await AuthenticatedClientAsync("notcounted_pmo@pulse.io");

        var report = (await (await pmo.GetAsync("/api/v1/reports/pmo")).Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts))!.Data!;

        var entry = report.Teams.Single(t => t.TeamId == team.Id).Engineers.Single(e => e.EngineerId == engineer.Id);
        entry.ActiveTasks.Should().Be(1, "only the live task counts toward workload");
        entry.TotalPoints.Should().Be(5);
        entry.PausedTasks.Should().Be(1);
        entry.UnestimatedBacklogTasks.Should().Be(1);
    }
}
