using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Reports;
using Pulse.Application.Reports.Queries;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Reports;

/// <summary>Covers the three reporting fixes: InQa-stage work is visible separately from current
/// workload, completed subtasks surface as their own count, and a backend engineer keeps delivery
/// credit after a task hands off to frontend.</summary>
[Collection("Integration")]
public class ReportingCreditAndVisibilityTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ReportingCreditAndVisibilityTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task PmoReport_shows_an_InQa_task_separately_without_counting_it_as_active_workload()
    {
        var team = await SeedTeamAsync("Utilization InQa Team");
        var engineer = await SeedEngineerAsync("util_inqa_eng@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Utilization InQa Project", team.Id);
        var task = await SeedTaskAsync("Awaiting QA", project.Id, points: 5, assigneeId: engineer.Id, requiresQa: true);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.SendToQa(engineer.Id);
            await db.SaveChangesAsync();
        }

        await SeedEngineerAsync("util_inqa_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("util_inqa_pmo@pulse.io");

        var response = await pmoClient.GetAsync("/api/v1/reports/pmo");
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);

        var teamEntry = body!.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        var engEntry = teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject;
        engEntry.TasksInQa.Should().Be(1);
        engEntry.PointsInQa.Should().Be(5);
        engEntry.ActiveTasks.Should().Be(0, "an InQa task is not current workload — only the new, separate InQa columns should reflect it");
        engEntry.TotalPoints.Should().Be(0);
    }

    [Fact]
    public async Task PmoReport_counts_a_completed_subtask_even_though_the_parent_task_is_not_done()
    {
        var team = await SeedTeamAsync("Utilization Subtask Team");
        var engineer = await SeedEngineerAsync("util_subtask_eng@pulse.io");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync("Utilization Subtask Project", team.Id);
        var task = await SeedTaskAsync("Parent still in progress", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("util_subtask_eng@pulse.io");

        var created = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Finished piece of work" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });

        await SeedEngineerAsync("util_subtask_pmo@pulse.io", Roles.HeadOfPmo);
        var pmoClient = await AuthenticatedClientAsync("util_subtask_pmo@pulse.io");

        var response = await pmoClient.GetAsync("/api/v1/reports/pmo");
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PmoReportDto>>(JsonOpts);

        var teamEntry = body!.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        var engEntry = teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Subject;
        engEntry.SubtasksCompleted.Should().Be(1);
    }

    [Fact]
    public async Task Backfill_credits_past_handoff_and_loan_completions_and_leaves_everything_else_alone()
    {
        var backend = await SeedEngineerAsync($"backfill_backend_{Guid.NewGuid():N}@pulse.io");
        var frontend = await SeedEngineerAsync($"backfill_frontend_{Guid.NewGuid():N}@pulse.io");
        var lender = await SeedEngineerAsync($"backfill_lender_{Guid.NewGuid():N}@pulse.io");
        var borrower = await SeedEngineerAsync($"backfill_borrower_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Backfill project");
        var handoff = await SeedTaskAsync("Past handoff", project.Id, points: 8, assigneeId: backend.Id);
        var loan = await SeedTaskAsync("Past loan", project.Id, points: 5, assigneeId: lender.Id);
        var plain = await SeedTaskAsync("Plain completion", project.Id, points: 3, assigneeId: backend.Id);
        var alreadyCredited = await SeedTaskAsync("Already credited", project.Id, points: 2, assigneeId: backend.Id);
        var qaSubtask = await SeedTaskAsync("QA sub-task", project.Id, points: 1, assigneeId: backend.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tasks = await db.Tasks.Where(t => new[] { handoff.Id, loan.Id, plain.Id, alreadyCredited.Id, qaSubtask.Id }.Contains(t.Id)).ToDictionaryAsync(t => t.Id);

            foreach (var id in new[] { handoff.Id, alreadyCredited.Id, qaSubtask.Id })
            {
                tasks[id].SetRequiresFrontendHandoff(true);
                tasks[id].HandOffToFrontend(frontend.Id, backend.Id);
            }
            tasks[qaSubtask.Id].SetParentTaskId(Guid.NewGuid());
            tasks[loan.Id].Loan(borrower.Id, lender.Id);
            foreach (var t in tasks.Values) t.MarkDone(Guid.NewGuid());
            await db.SaveChangesAsync();

            // Recreate the pre-CreditedEngineerId state: the Done rows carry no credit.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE task_history SET credited_engineer_id = NULL WHERE field = 'status' AND to_value = 'Done' AND task_id = ANY({0})",
                new[] { handoff.Id, loan.Id, plain.Id, qaSubtask.Id });
        }

        async Task<Dictionary<Guid, Guid?>> CreditsAsync()
        {
            using var s = Factory.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<PulseDbContext>();
            var ids = new[] { handoff.Id, loan.Id, plain.Id, alreadyCredited.Id, qaSubtask.Id };
            return await db.TaskHistory
                .Where(h => ids.Contains(h.TaskId) && h.Field == "status" && h.NewValue == "Done")
                .ToDictionaryAsync(h => h.TaskId, h => h.CreditedEngineerId);
        }

        var before = await CreditsAsync();
        before[handoff.Id].Should().BeNull();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            await db.Database.ExecuteSqlRawAsync(CreditedEngineerBackfill.Sql);
        }

        var after = await CreditsAsync();
        after[handoff.Id].Should().Be(backend.Id, "a past backend->frontend handoff credits the backend engineer");
        after[loan.Id].Should().Be(lender.Id, "a past loan credits the lender");
        after[plain.Id].Should().BeNull("with neither field set nothing is guessed — reports keep falling back to the assignee");
        after[alreadyCredited.Id].Should().Be(backend.Id, "an already-credited row is never rewritten");
        after[qaSubtask.Id].Should().BeNull("QA sub-tasks aren't part of delivery metrics");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.Database.ExecuteSqlRawAsync(CreditedEngineerBackfill.Sql)).Should().Be(0, "re-running changes nothing");
        }

        using var scope2 = Factory.Services.CreateScope();
        var repo = scope2.ServiceProvider.GetRequiredService<ITaskRepository>();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddHours(1);
        (await repo.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { backend.Id })).Should().Be(8 + 3 + 2);
        (await repo.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { lender.Id })).Should().Be(5);
        (await repo.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { frontend.Id, borrower.Id })).Should().Be(0);
    }

    [Fact]
    public async Task A_task_handed_off_backend_to_frontend_and_completed_still_credits_the_backend_engineer()
    {
        var backendEngineer = await SeedEngineerAsync($"handoff_credit_backend_{Guid.NewGuid():N}@pulse.io");
        var frontendEngineer = await SeedEngineerAsync($"handoff_credit_frontend_{Guid.NewGuid():N}@pulse.io");
        var project = await SeedProjectAsync("Handoff credit project");
        var task = await SeedTaskAsync("Full-stack feature", project.Id, points: 8, assigneeId: backendEngineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.SetRequiresFrontendHandoff(true);
            tracked.HandOffToFrontend(frontendEngineer.Id, backendEngineer.Id);
            await db.SaveChangesAsync();
        }

        await MarkTaskDoneAsync(task.Id);

        using var scope2 = Factory.Services.CreateScope();
        var tasks = scope2.ServiceProvider.GetRequiredService<ITaskRepository>();
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddHours(1);

        var backendPoints = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { backendEngineer.Id });
        var frontendPoints = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(from, to, new[] { frontendEngineer.Id });
        backendPoints.Should().Be(8, "the backend engineer did their part of the work before handing it off");
        frontendPoints.Should().Be(0, "crediting both sides would double count a single piece of delivered work");

        var counts = await tasks.GetCompletedTaskCountByEngineerInRangeAsync(from, to);
        counts.GetValueOrDefault(backendEngineer.Id).Should().Be(1);
        counts.GetValueOrDefault(frontendEngineer.Id).Should().Be(0);

        var stats = await tasks.GetPerformanceStatsAsync(backendEngineer.Id, from, to);
        stats.DeliveredPoints.Should().Be(8);
        stats.TasksCompleted.Should().Be(1);

        var throughput = await tasks.GetWeeklyThroughputByAssigneeAsync(backendEngineer.Id);
        throughput.Sum(p => p.PointsDelivered).Should().Be(8);
    }
}
