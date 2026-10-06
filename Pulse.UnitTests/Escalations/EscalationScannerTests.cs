using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Domain.Escalations;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Escalations;

public class EscalationScannerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEscalationEventRepository> _events = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly OverworkThresholds _thresholds = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public EscalationScannerTests()
    {
        // Personal tasks are read from their own query; most tests have none.
        _tasks.Setup(r => r.GetPersonalEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PulseTask>());
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private EscalationScanner CreateScanner() =>
        new(_tasks.Object, _events.Object, _engineers.Object, _teams.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _thresholds, _access.Object);

    private static PulseTask ShortTask(DateOnly dueDate)
    {
        // A task under escalation observation is always assigned — Backlog (no assignee)
        // is excluded from the scanner entirely, so tests need a real assignee to reach it.
        // Assign with a safe future due date first, then set the real (possibly past) one
        // afterward: PromoteFromBacklogIfGroomed corrects a due date that lapsed while still in
        // Backlog, which would otherwise mask exactly the overdue scenario these tests exercise.
        var task = PulseTask.Create("Test task", 3, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria, task.Points, dueDate, Guid.NewGuid());
        return task;
    }

    private static void SetActivatedAt(PulseTask task, DateTime value)
    {
        var prop = typeof(PulseTask).GetProperty("ActivatedAt")!;
        prop.SetValue(task, value);
    }

    [Fact]
    public async Task NoFire_WhenTaskJustAssigned_ShortDuration()
    {
        // 2-day task assigned right now → 0% elapsed, well below the 60% T-3 threshold
        var task = ShortTask(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FiresT3_OnShortTask_WhenPastElapsedThreshold()
    {
        // ActivatedAt = 5 days ago, DueDate = 2 days from now.
        // totalDays ≈ 6–7 days (< 7.5 crossover), so t3Threshold = 0.60.
        // elapsedPct = 5 / (6–7) ≈ 0.71–0.83 → reliably above 0.60 and below 0.85 (t1 threshold).
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(task.Id, EscalationLevel.TMinus3, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DoesNotRefire_WhenEventAlreadyFired()
    {
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FiresOverdue_WhenTaskIsPastDueDate()
    {
        // DueDate in the past → daysUntilDue < 0 → immediate Overdue regardless of elapsed %.
        // ActivatedAt pushed back beyond the reactivation grace window — this test is about a task
        // that's genuinely been overdue a while, not one that just reactivated moments ago.
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<Engineer>());
        _notifications.Setup(n => n.SaveChangesAsync(It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FiresOverdue_ForQaTask_OnlyNotifiesHeadOfPmo_NotTheFullPmAndHeadBroadcast()
    {
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));
        task.SetParentTaskId(Guid.NewGuid()); // marks it as a QA review task

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        _engineers.Setup(e => e.GetByIdAsync(task.AssigneeId!.Value, It.IsAny<CancellationToken>()))
                  .ReturnsAsync((Engineer?)null);
        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<Engineer>());
        _notifications.Setup(n => n.SaveChangesAsync(It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        await CreateScanner().RunAsync();

        _engineers.Verify(e => e.ListByRoleAsync(Roles.HeadOfPmo, It.IsAny<CancellationToken>()), Times.Once);
        _engineers.Verify(e => e.ListByRoleAsync(Roles.ProjectManager, It.IsAny<CancellationToken>()), Times.Never);
        _engineers.Verify(e => e.ListByRoleAsync(Roles.HeadOfRnD, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FiresOverdue_ForNonQaTask_OnlyNotifiesManagersWhoCanAccessTheProject()
    {
        // A department head outside this project's own department used to get the same
        // "overdue" ping as everyone else and just 403'd trying to open it.
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = ShortTask(dueDate); // no ParentTaskId — the full PM/head broadcast path
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));

        var pm = Engineer.Create("Priya PM", "priya@test.io", "hash", Roles.ProjectManager, 20, 14);
        var headWithAccess = Engineer.Create("Head With Access", "headyes@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        var headWithoutAccess = Engineer.Create("Head Without Access", "headno@test.io", "hash", Roles.HeadOfDesign, 20, 14);

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        _engineers.Setup(e => e.GetByIdAsync(task.AssigneeId!.Value, It.IsAny<CancellationToken>()))
                  .ReturnsAsync((Engineer?)null);
        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<Engineer>());
        _engineers.Setup(e => e.ListByRoleAsync(Roles.ProjectManager, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { pm });
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfRnD, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { headWithAccess });
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfDesign, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { headWithoutAccess });
        _notifications.Setup(n => n.SaveChangesAsync(It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, headWithoutAccess.Id, Roles.HeadOfDesign, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        await CreateScanner().RunAsync();

        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == pm.Id), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == headWithAccess.Id), It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == headWithoutAccess.Id), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoesNotFire_ForTaskInQa()
    {
        // Ownership has moved to QA — even a badly overdue task should not escalate against the original engineer.
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = ShortTask(dueDate);
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DoesNotFire_ForBacklogTask()
    {
        // No assignee yet — not real work in progress, so it shouldn't escalate against anyone.
        // Uses PulseTask.Create directly (not the ShortTask helper) so the task stays unassigned.
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = PulseTask.Create("Unassigned task", 3, Guid.NewGuid(), dueDate: dueDate);

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DoesNotFire_ForPausedTask()
    {
        // A voluntary hold shouldn't escalate against the engineer either, even past due.
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var task = ShortTask(dueDate);
        task.Pause("Reprioritized", Guid.NewGuid());

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FiresOverdue_ForZeroDurationTask()
    {
        // ActivatedAt set to after the DueDate → totalDays ≤ 0 → Overdue even though not yet past due
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var task = ShortTask(dueDate);
        // Set ActivatedAt one day past dueDateTime so totalDays is negative
        var dueDateTime = dueDate.ToDateTime(TimeOnly.MinValue);
        SetActivatedAt(task, dueDateTime.AddDays(1));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);
        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<Engineer>());
        _notifications.Setup(n => n.SaveChangesAsync(It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(task.Id, EscalationLevel.Overdue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SuppressesT3_WhenHoursRemainingBelowFloor()
    {
        // Task due in 2 days, activated 12 days ago.
        // totalDays ≈ 14, elapsedPct ≈ 0.83 — past T-3 threshold (≈ 0.79), never T-1 (≈ 0.93).
        // hoursRemaining ≈ 48–72h. Floor set to 100h (always above hoursRemaining) → suppressed.
        _thresholds.EscalationT3MinHours = 100.0;

        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-12));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SuppressesT1_WhenHoursRemainingBelowFloor()
    {
        // Same geometry — elapsedPct ≈ 0.83, in T-3 range.
        // Both floors set to 100h so neither T-3 nor T-1 can fire.
        _thresholds.EscalationT3MinHours = 100.0;
        _thresholds.EscalationT1MinHours = 100.0;

        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-12));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EscalationFires_WhenHoursRemainingAboveFloor()
    {
        // Proves the floor gate doesn't suppress a valid alert when hours remaining > floor.
        // Setting both floors to 0 guarantees neither can block. Elapsed pct is in T-3 range.
        // The same scenario with floor = 100 is what SuppressesT3 tests — verifying the inverse.
        _thresholds.EscalationT3MinHours = 0.0;
        _thresholds.EscalationT1MinHours = 0.0;

        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var task = ShortTask(dueDate);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-12));

        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { task });
        _events.Setup(e => e.AlreadyFiredAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        await CreateScanner().RunAsync();

        _events.Verify(e => e.RecordAsync(It.IsAny<Guid>(), It.IsAny<EscalationLevel>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_personal_task_reminds_only_its_owner_and_never_the_team_lead_or_managers()
    {
        var owner = Engineer.Create("Hannah HR", "hannah@test.io", "hash", Roles.HR, 20, 14);
        var teamLead = Engineer.Create("Lead Lara", "lara@test.io", "hash", Roles.TeamLead, 20, 14);
        var pm = Engineer.Create("PM Pat", "pat@test.io", "hash", Roles.ProjectManager, 20, 14);
        var task = ShortTask(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        task.Assign(owner.Id, owner.Id);
        SetActivatedAt(task, DateTime.UtcNow.AddDays(-5));

        _tasks.Setup(r => r.GetPersonalEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { task });
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PulseTask>());
        _events.Setup(e => e.AlreadyFiredAsync(task.Id, It.IsAny<EscalationLevel>(), default)).ReturnsAsync(false);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _engineers.Setup(e => e.ListByRoleAsync(Roles.ProjectManager, default)).ReturnsAsync(new[] { pm });
        _engineers.Setup(e => e.ListByRoleAsync(Roles.TeamLead, default)).ReturnsAsync(new[] { teamLead });
        var notified = new List<Guid>();
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => notified.Add(n.UserId)).Returns(Task.CompletedTask);

        await CreateScanner().RunAsync();

        notified.Should().Equal(owner.Id);
    }
}
