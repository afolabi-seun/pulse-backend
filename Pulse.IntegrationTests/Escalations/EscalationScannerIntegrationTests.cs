using Pulse.Application.Escalations;
using Pulse.Domain.Engineers;
using Pulse.Domain.Escalations;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Escalations;

/// <summary>
/// Integration tests for EscalationScanner.RunAsync verifying the dual-trigger formula
/// (fire at the later of: X% elapsed OR Y days remaining) with real DB and a no-op email stub.
/// </summary>
[Collection("Integration")]
public class EscalationScannerIntegrationTests : IClassFixture<PulseWebApplicationFactory>
{
    private readonly PulseWebApplicationFactory _factory;

    public EscalationScannerIntegrationTests(PulseWebApplicationFactory factory) =>
        _factory = factory;

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<(PulseTask Task, Guid ProjectId)> SeedTaskAsync(
        DateOnly dueDate,
        DateTime activatedAt,
        Guid? assigneeId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        // A unique code per call — this helper is called by many tests in this file, and
        // Project.Create's own name-derived fallback isn't unique across calls with the same name.
        var project = Project.Create("Escalation Test Project", code: $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant());
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        PulseTask task;
        if (assigneeId.HasValue)
        {
            // Assign with a safe future due date first, then set the real (possibly past) one
            // afterward: PromoteFromBacklogIfGroomed silently corrects a due date that lapsed
            // while still in Backlog, which would otherwise mask any test passing a past dueDate
            // here (every existing caller in this file happens to use a future one, so this never
            // bit until a reactivation-grace-period test needed a genuinely overdue due date).
            task = PulseTask.Create("Test Task", 3, project.Id, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));
            task.Assign(assigneeId.Value, assigneeId.Value);
            task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria, task.Points, dueDate, assigneeId.Value);
        }
        else
        {
            task = PulseTask.Create("Test Task", 3, project.Id, dueDate: dueDate);
        }

        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        // Override ActivatedAt via raw SQL since it's a private setter
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tasks SET activated_at = {activatedAt} WHERE id = {task.Id}");

        return (task, project.Id);
    }

    private async Task<Engineer> SeedEngineerAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();
        var engineer = Engineer.Create("Scanner Test", email, hasher.Hash("Str0ng!Pass12"), Roles.Engineer, 20, 14);
        db.Engineers.Add(engineer);
        await db.SaveChangesAsync();
        return engineer;
    }

    private async Task RunScannerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<EscalationScanner>();
        await scanner.RunAsync();
    }

    private async Task<IReadOnlyList<EscalationEvent>> GetEventsForTaskAsync(Guid taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return await db.EscalationEvents
            .Where(e => e.TaskId == taskId)
            .ToListAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LongDurationTask_T3_fires_when_elapsed_pct_exceeds_threshold()
    {
        // 14-day task, 12 days elapsed → elapsedPct ≈ 0.86 > t3Threshold ≈ 0.79
        var now         = DateTime.UtcNow;
        var dueDate     = DateOnly.FromDateTime(now.AddDays(2));
        var activatedAt = now.AddDays(-12);
        var engineer    = await SeedEngineerAsync($"esc_t3_{Guid.NewGuid():N}@pulse.io");
        var (task, _)   = await SeedTaskAsync(dueDate, activatedAt, engineer.Id);

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().Contain(e => e.Level == EscalationLevel.TMinus3,
            "elapsed percentage (≈ 86%) exceeds the T-3 dual-trigger threshold (≈ 79%) for a 14-day task");
    }

    [Fact]
    public async Task ShortDurationTask_no_escalation_fires_when_elapsed_pct_below_threshold()
    {
        // 2-day task, activated just now → elapsedPct ≈ 0 < t3Threshold = 0.60
        var now         = DateTime.UtcNow;
        var dueDate     = DateOnly.FromDateTime(now.AddDays(2));
        var activatedAt = now;
        var engineer    = await SeedEngineerAsync($"esc_short_{Guid.NewGuid():N}@pulse.io");
        var (task, _)   = await SeedTaskAsync(dueDate, activatedAt, engineer.Id);

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().BeEmpty(
            "elapsed percentage (≈ 0%) is below every escalation threshold for a freshly activated 2-day task");
    }

    [Fact]
    public async Task ZeroDurationTask_fires_Overdue_when_activatedAt_is_after_dueDate()
    {
        // activatedAt > dueDateTime → totalDays < 0 → scanner treats as immediately Overdue
        var now         = DateTime.UtcNow;
        var dueDate     = DateOnly.FromDateTime(now.AddDays(1));
        var activatedAt = now.AddDays(3); // activated AFTER the due date
        var engineer    = await SeedEngineerAsync($"esc_zero_{Guid.NewGuid():N}@pulse.io");
        var (task, _)   = await SeedTaskAsync(dueDate, activatedAt, engineer.Id);

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().Contain(e => e.Level == EscalationLevel.Overdue,
            "a task whose activatedAt is after its dueDate has totalDays ≤ 0, which the scanner maps to Overdue");
    }

    [Fact]
    public async Task ReactivatedTask_no_Overdue_within_the_grace_window_even_though_the_due_date_has_passed()
    {
        // Simulates a task reactivated from a QA rejection moments ago, whose due date was already
        // behind it by then (e.g. it sat in QA past its deadline) — the fresh activatedAt should
        // buy it a grace window instead of immediately firing the full Overdue broadcast.
        var now         = DateTime.UtcNow;
        var dueDate     = DateOnly.FromDateTime(now.AddDays(-10));
        var activatedAt = now.AddMinutes(-5);
        var engineer    = await SeedEngineerAsync($"esc_grace_{Guid.NewGuid():N}@pulse.io");
        var (task, _)   = await SeedTaskAsync(dueDate, activatedAt, engineer.Id);

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().BeEmpty("the task reactivated 5 minutes ago, well within the reactivation grace window");
    }

    [Fact]
    public async Task ReactivatedTask_fires_Overdue_once_the_grace_window_expires()
    {
        var now         = DateTime.UtcNow;
        var dueDate     = DateOnly.FromDateTime(now.AddDays(-10));
        var activatedAt = now.AddDays(-2); // well past the default 24h grace window
        var engineer    = await SeedEngineerAsync($"esc_grace_expired_{Guid.NewGuid():N}@pulse.io");
        var (task, _)   = await SeedTaskAsync(dueDate, activatedAt, engineer.Id);

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().Contain(e => e.Level == EscalationLevel.Overdue,
            "activated 2 days ago, well outside the reactivation grace window, with a due date 10 days in the past");
    }

    [Fact]
    public async Task RecalledTask_no_Overdue_within_the_grace_window_even_though_the_due_date_has_passed()
    {
        // A task loaned out, then recalled after its due date already passed — Recall() funnels
        // through Assign(), which resets ActivatedAt the same way ConfirmQaRejection does, so it
        // gets the same reactivation grace window with no extra escalation code needed for this
        // path (see PulseTask.Assign/Loan/Recall).
        var now      = DateTime.UtcNow;
        var dueDate  = DateOnly.FromDateTime(now.AddDays(-10));
        var lender   = await SeedEngineerAsync($"esc_recall_lender_{Guid.NewGuid():N}@pulse.io");
        var borrower = await SeedEngineerAsync($"esc_recall_borrower_{Guid.NewGuid():N}@pulse.io");
        var (task, _) = await SeedTaskAsync(dueDate, now.AddDays(-5), lender.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var tracked = await db.Tasks.FirstAsync(t => t.Id == task.Id);
            tracked.Loan(borrower.Id, lender.Id);
            await db.SaveChangesAsync();
            tracked.Recall(lender.Id);
            await db.SaveChangesAsync();
        }

        await RunScannerAsync();

        var events = await GetEventsForTaskAsync(task.Id);
        events.Should().BeEmpty("the task was just recalled, resetting ActivatedAt within the grace window");
    }
}
