using System.Net;
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

/// <summary>Covers the PMO report's two derived-from-TaskHistory metrics added alongside the
/// Dashboard's Organization overview: org-wide average cycle time and average PR approval time.</summary>
[Collection("Integration")]
public class PmoReportCycleMetricsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PmoReportCycleMetricsTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task BackdateTaskHistoryAsync(Guid taskId, string field, string toValue, DateTime changedAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE task_history SET ts = {changedAt} WHERE task_id = {taskId} AND field = {field} AND to_value = {toValue}");
    }

    [Fact]
    public async Task Pr_approval_time_pairs_with_the_most_recent_request_not_an_earlier_rejected_one()
    {
        var team = await SeedTeamAsync("Approval Team");
        var engineer = await SeedEngineerAsync("pr_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var pmo = await SeedEngineerAsync("pr_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Approval Project", team.Id);
        var task = await SeedTaskAsync("Needs PR approval", project.Id, assigneeId: engineer.Id, requiresPrApproval: true);

        var engineerClient = await AuthenticatedClientAsync("pr_eng@pulse.io");
        var pmoClient = await AuthenticatedClientAsync("pr_pmo@pulse.io");

        // First cycle: requested long ago, then rejected — must NOT count toward the average.
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request", new { prLink = "https://git/pr/1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await BackdateTaskHistoryAsync(task.Id, "pr_link", "https://git/pr/1", DateTime.UtcNow.AddDays(-10));
        (await pmoClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/reject", new { reason = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second cycle: the one that actually gets approved — the only duration that should feed
        // the average.
        (await engineerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request", new { prLink = "https://git/pr/2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await pmoClient.PostAsync($"/api/v1/tasks/{task.Id}/pr-approval/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await pmoClient.GetAsync("/api/v1/reports/pmo");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = (await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts))!.Data!;

        report.AvgPrApprovalHours.Should().NotBeNull();
        // The real second-cycle gap is milliseconds; if the (wrongly) backdated first request had
        // been paired instead, this would read as roughly 240 hours (10 days).
        report.AvgPrApprovalHours!.Value.Should().BeLessThan(1);
    }

    [Fact]
    public async Task Cycle_time_reflects_days_from_creation_to_completion()
    {
        var team = await SeedTeamAsync("Cycle Team");
        var engineer = await SeedEngineerAsync("cycle_eng@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var pmo = await SeedEngineerAsync("cycle_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Cycle Project", team.Id);
        var task = await SeedTaskAsync("Finishes this week", project.Id, assigneeId: engineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE tasks SET created_at = {DateTime.UtcNow.AddDays(-3)} WHERE id = {task.Id}");
        }

        var engineerClient = await AuthenticatedClientAsync("cycle_eng@pulse.io");
        var pmoClient = await AuthenticatedClientAsync("cycle_pmo@pulse.io");
        (await engineerClient.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await pmoClient.GetAsync("/api/v1/reports/pmo");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = (await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts))!.Data!;

        report.AvgCycleTimeDays.Should().Be(3);
    }
}
