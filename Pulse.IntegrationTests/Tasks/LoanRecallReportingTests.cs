using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Covers the two "reporting clarity" fixes: delivery credit for a task completed while
/// on loan now goes to the lending engineer (CreditedEngineerId), not whoever currently holds it;
/// and the project Activity feed distinguishes a loan/recall from a plain reassignment.</summary>
[Collection("Integration")]
public class LoanRecallReportingTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public LoanRecallReportingTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task A_task_completed_while_on_loan_credits_the_lending_engineer_not_the_borrower()
    {
        var lender = await SeedEngineerAsync($"credit_lender_{Guid.NewGuid():N}@pulse.io");
        var borrower = await SeedEngineerAsync($"credit_borrower_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Credit attribution project");
        var task = await SeedTaskAsync("Loaned-out work", project.Id, points: 5, assigneeId: lender.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.Loan(borrower.Id, lender.Id);
            await db.SaveChangesAsync();
        }

        await MarkTaskDoneAsync(task.Id);

        using var scope2 = Factory.Services.CreateScope();
        var tasks = scope2.ServiceProvider.GetRequiredService<ITaskRepository>();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddHours(1);

        var lenderPoints = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { lender.Id });
        var borrowerPoints = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { borrower.Id });
        lenderPoints.Should().Be(5, "the lending engineer's team committed the work, even though the borrower finished it");
        borrowerPoints.Should().Be(0, "crediting both sides would double count a single piece of delivered work");

        var counts = await tasks.GetCompletedTaskCountByEngineerInRangeAsync(from, to);
        counts.GetValueOrDefault(lender.Id).Should().Be(1);
        counts.GetValueOrDefault(borrower.Id).Should().Be(0);
    }

    [Fact]
    public async Task A_task_completed_without_ever_being_loaned_still_credits_the_current_assignee()
    {
        var engineer = await SeedEngineerAsync($"credit_plain_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Plain credit project");
        var task = await SeedTaskAsync("Never loaned work", project.Id, points: 3, assigneeId: engineer.Id);

        await MarkTaskDoneAsync(task.Id);

        using var scope = Factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddHours(1);

        var points = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { engineer.Id });
        points.Should().Be(3);
    }

    [Fact]
    public async Task Project_activity_feed_distinguishes_loan_and_recall_from_a_plain_reassignment()
    {
        var lender = await SeedEngineerAsync($"activity_lender_{Guid.NewGuid():N}@pulse.io");
        var borrower = await SeedEngineerAsync($"activity_borrower_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Activity wording project");
        var task = await SeedTaskAsync("Visible in the activity feed", project.Id, assigneeId: lender.Id);
        var pm = await SeedEngineerAsync($"activity_pm_{Guid.NewGuid():N}@pulse.io", Roles.ProjectManager);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.Loan(borrower.Id, lender.Id);
            await db.SaveChangesAsync();
            tracked.Recall(lender.Id);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync(pm.Email);
        var resp = await client.GetAsync($"/api/v1/projects/{project.Id}/activity");
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts);

        var summaries = body!.Data!.Items.Where(i => i.TaskId == task.Id).Select(i => i.Summary).ToList();
        summaries.Should().Contain(s => s.Contains("loaned to"));
        summaries.Should().Contain(s => s.Contains("recalled back to"));
        summaries.Should().NotContain(s => s.StartsWith("reassigned from"),
            "a loan/recall must not also read as a generic reassignment");
    }
}
