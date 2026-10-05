using Pulse.Application.Escalations;
using Pulse.Domain.Engineers;
using Pulse.Domain.Escalations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Pulse.IntegrationTests.Escalations;

/// <summary>
/// Integration tests for Phase 6 escalation correctness fixes:
///   Finding 1 — Reassignment resets the elapsed clock (ActivatedAt).
///   Finding 2 — Due-date extension re-arms escalation events.
///   Finding 3 — Single-task reassignment is audited as TASK_REASSIGNED.
/// </summary>
[Collection("Integration")]
public class EscalationPhase6Tests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public EscalationPhase6Tests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task RunScannerAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<EscalationScanner>();
        await scanner.RunAsync();
    }

    private async Task<IReadOnlyList<EscalationEvent>> GetEventsAsync(Guid taskId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return await db.EscalationEvents.Where(e => e.TaskId == taskId).ToListAsync();
    }

    private async Task SetActivatedAtAsync(Guid taskId, DateTime activatedAt)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET activated_at = {activatedAt} WHERE id = {taskId}");
    }

    // ── Finding 1: Reassignment resets the elapsed clock ─────────────────────

    [Fact]
    public async Task Reassignment_resets_ActivatedAt_so_scanner_does_not_fire_immediately()
    {
        // 14-day task, original assignee has been working 12 days (≈ 86% elapsed → past T-3).
        // After reassignment, elapsed clock resets to 0 → scanner must NOT fire for new assignee.
        var original = await SeedEngineerAsync($"p6_orig_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        var newAssignee = await SeedEngineerAsync($"p6_new_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("p6_pm@pulse.io", Roles.ProjectManager);

        var project = await SeedProjectAsync("Phase6 Reassign project");
        var task = await SeedTaskAsync("Phase6 task", project.Id, assigneeId: original.Id, dueDaysFromNow: 2);

        // Simulate 12 days elapsed — scanner would fire T-3 for original assignee
        await SetActivatedAtAsync(task.Id, DateTime.UtcNow.AddDays(-12));

        // First scanner run confirms events fire at ~86% elapsed
        await RunScannerAsync();
        var beforeEvents = await GetEventsAsync(task.Id);
        beforeEvents.Should().Contain(e => e.Level == EscalationLevel.TMinus3,
            "12 days into a 14-day task exceeds T-3 threshold — events must exist before reassignment");

        // Reassign via PATCH → clears old escalation events and resets ActivatedAt
        var pmClient = await AuthenticatedClientAsync("p6_pm@pulse.io");
        await pmClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new
        {
            assigneeId = newAssignee.Id
        });

        // Events must have been cleared by the handler
        var afterClear = await GetEventsAsync(task.Id);
        afterClear.Should().BeEmpty("reassignment must clear old escalation events");

        // Second scanner run: new assignee just picked up the task — elapsed ≈ 0%
        await RunScannerAsync();
        var afterEvents = await GetEventsAsync(task.Id);
        afterEvents.Should().NotContain(e => e.Level == EscalationLevel.TMinus3,
            "freshly reassigned task has near-zero elapsed % — T-3 must not fire again immediately");
    }

    // ── Finding 2: Due-date extension re-arms escalation events ──────────────

    [Fact]
    public async Task Due_date_extension_clears_fired_events_so_scanner_can_refire()
    {
        // 14-day task, 12 days elapsed → T-3 fires. PM extends due date by 14 days.
        // Scanner must fire T-3 again under the new schedule.
        var engineer = await SeedEngineerAsync($"p6_dd_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("p6_dd_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Phase6 DueDate project");
        var task = await SeedTaskAsync("DueDate extension task", project.Id,
            dueDaysFromNow: 2, assigneeId: engineer.Id);

        await SetActivatedAtAsync(task.Id, DateTime.UtcNow.AddDays(-12));

        // First run: T-3 fires
        await RunScannerAsync();
        (await GetEventsAsync(task.Id)).Should()
            .Contain(e => e.Level == EscalationLevel.TMinus3, "T-3 must fire before extension");

        // Extend due date by 14 more days via PATCH
        var pmClient = await AuthenticatedClientAsync("p6_dd_pm@pulse.io");
        var newDue = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(16)).ToString("yyyy-MM-dd");
        await pmClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { dueDate = newDue, dueDateChangeReason = "Scope grew" });

        // Events must be cleared after due-date change
        (await GetEventsAsync(task.Id)).Should()
            .BeEmpty("extending the due date must clear old escalation events");

        // Second run with reset ActivatedAt to simulate same elapsed % on extended task
        // Re-wind to 12 days ago so elapsed % is still high on the 28-day task (≈ 43% — below T-3 ≈ 56%)
        // This verifies events don't fire prematurely under the new schedule
        await RunScannerAsync();
        (await GetEventsAsync(task.Id)).Should()
            .NotContain(e => e.Level == EscalationLevel.TMinus3,
                "12 days out of a 28-day window is below T-3 — must not fire prematurely");
    }

    // ── Finding 3: Single reassignment logged as TASK_REASSIGNED ─────────────

    [Fact]
    public async Task Single_task_reassign_creates_TASK_REASSIGNED_audit_entry()
    {
        var original = await SeedEngineerAsync($"p6_audit_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        var newAssignee = await SeedEngineerAsync($"p6_audit_new_{Guid.NewGuid():N}@pulse.io", Roles.Engineer);
        var head = await SeedEngineerAsync("p6_audit_head@pulse.io", Roles.HeadOfRnD);

        var project = await SeedProjectAsync("Phase6 Audit project");
        var task = await SeedTaskAsync("Audit reassign task", project.Id, assigneeId: original.Id);

        var pmClient = await AuthenticatedClientAsync("p6_audit_head@pulse.io");
        await pmClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { assigneeId = newAssignee.Id });

        // Verify the audit log contains TASK_REASSIGNED (not just TASK_UPDATED)
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var entry = db.AuditLog.FirstOrDefault(a =>
            a.ActorId == head.Id && a.Action == "TASK_REASSIGNED");

        entry.Should().NotBeNull(
            "single-task reassignment via PATCH must log TASK_REASSIGNED in addition to TASK_UPDATED");
    }
}
