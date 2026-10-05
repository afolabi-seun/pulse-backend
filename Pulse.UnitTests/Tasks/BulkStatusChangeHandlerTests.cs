using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class BulkStatusChangeHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _auditLog = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();

    public BulkStatusChangeHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _checkIns
            .Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CheckIn>());
    }

    private BulkStatusChangeHandler CreateHandler() =>
        new(_tasks.Object, _auditLog.Object, _access.Object, _checkIns.Object);

    private static PulseTask ActiveTask() =>
        PulseTask.Create("Task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));

    private static PulseTask BlockedTask()
    {
        var t = ActiveTask();
        t.FlagBlocker("waiting on API", Guid.NewGuid());
        return t;
    }

    private static PulseTask DoneTask()
    {
        var t = ActiveTask();
        t.MarkDone(Guid.NewGuid());
        return t;
    }

    // A parent task awaiting QA, with its linked QA sub-task still open — the shape
    // SendToQaHandler produces, and what BulkStatusChange's cascade needs to close correctly.
    private static (PulseTask Parent, PulseTask QaTask) InQaTaskWithOpenQaSubtask()
    {
        var parent = ActiveTask();
        parent.SetRequiresQa(true);
        var qaTask = PulseTask.Create($"[QA] {parent.Title}", parent.Points, parent.ProjectId,
            TaskType.Review, parent.DueDate, Guid.NewGuid());
        qaTask.SetParentTaskId(parent.Id);
        parent.SendToQa(Guid.NewGuid());
        parent.SetQaTaskId(qaTask.Id);
        return (parent, qaTask);
    }

    [Fact]
    public async Task Returns_error_when_no_task_ids_provided()
    {
        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([], "done", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Returns_error_for_invalid_target_status()
    {
        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([Guid.NewGuid()], "blocked", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Bulk_done_marks_active_tasks_as_done_and_returns_count()
    {
        var actorId = Guid.NewGuid();
        var t1 = ActiveTask();
        var t2 = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(t1.Id, default)).ReturnsAsync(t1);
        _tasks.Setup(r => r.GetByIdAsync(t2.Id, default)).ReturnsAsync(t2);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([t1.Id, t2.Id], "done", actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(2);
        t1.Status.Should().Be(DomainTaskStatus.Done);
        t2.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task Bulk_done_skips_already_done_tasks()
    {
        var already = DoneTask();
        var active = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(already.Id, default)).ReturnsAsync(already);
        _tasks.Setup(r => r.GetByIdAsync(active.Id, default)).ReturnsAsync(active);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([already.Id, active.Id], "done", Guid.NewGuid(), null), default);

        result.Data.Should().Be(1);
    }

    [Fact]
    public async Task Bulk_active_unblocks_blocked_tasks()
    {
        var blocked = BlockedTask();
        _tasks.Setup(r => r.GetByIdAsync(blocked.Id, default)).ReturnsAsync(blocked);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([blocked.Id], "active", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(1);
        blocked.Status.Should().Be(DomainTaskStatus.Active);
        blocked.BlockerReason.Should().BeNull();
    }

    [Fact]
    public async Task Bulk_active_skips_already_active_tasks()
    {
        var active = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(active.Id, default)).ReturnsAsync(active);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([active.Id], "active", Guid.NewGuid(), null), default);

        result.Data.Should().Be(0);
    }

    [Fact]
    public async Task Skips_task_ids_not_found_in_repository()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([Guid.NewGuid(), Guid.NewGuid()], "done", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(0);
    }

    [Fact]
    public async Task Deduplicates_task_ids()
    {
        var taskId = Guid.NewGuid();
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([taskId, taskId], "done", Guid.NewGuid(), null), default);

        result.Data.Should().Be(1);
    }

    [Fact]
    public async Task Saves_changes_when_at_least_one_task_changed()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([task.Id], "done", Guid.NewGuid(), null), default);

        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Does_not_save_when_nothing_changed()
    {
        var done = DoneTask();
        _tasks.Setup(r => r.GetByIdAsync(done.Id, default)).ReturnsAsync(done);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([done.Id], "done", Guid.NewGuid(), null), default);

        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Bulk_done_creates_only_one_checkin_when_completing_multiple_tasks_in_the_same_project()
    {
        // GetByEngineerAndDateAsync reflects prior AddAsync calls (same as the real repository
        // would once AutoCheckIn saves immediately) — proves the second task in the batch
        // extends the first check-in instead of creating a duplicate for the same project.
        var actorId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var t1 = PulseTask.Create("Task one", 3, projectId, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        var t2 = PulseTask.Create("Task two", 3, projectId, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        _tasks.Setup(r => r.GetByIdAsync(t1.Id, default)).ReturnsAsync(t1);
        _tasks.Setup(r => r.GetByIdAsync(t2.Id, default)).ReturnsAsync(t2);

        CheckIn? createdCheckIn = null;
        _checkIns
            .Setup(c => c.GetByEngineerAndDateAsync(actorId, It.IsAny<DateOnly>(), projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => createdCheckIn);
        _checkIns
            .Setup(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()))
            .Callback<CheckIn, CancellationToken>((ci, _) => createdCheckIn = ci)
            .Returns(Task.CompletedTask);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([t1.Id, t2.Id], "done", actorId, null), default);

        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()), Times.Once);
        createdCheckIn!.Completed.Should().Contain("Task one").And.Contain("Task two");
    }

    [Fact]
    public async Task Bulk_done_creates_a_separate_checkin_per_project()
    {
        // The exact behavior the previous "only one check-in, period" version deliberately
        // avoided — completing work across different projects in one bulk action should show up
        // as one check-in per project, matching a manually-submitted check-in's own scoping.
        var actorId = Guid.NewGuid();
        var t1 = ActiveTask();
        var t2 = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(t1.Id, default)).ReturnsAsync(t1);
        _tasks.Setup(r => r.GetByIdAsync(t2.Id, default)).ReturnsAsync(t2);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([t1.Id, t2.Id], "done", actorId, null), default);

        _checkIns.Verify(c => c.AddAsync(
            It.Is<CheckIn>(ci => ci.ProjectId == t1.ProjectId), It.IsAny<CancellationToken>()), Times.Once);
        _checkIns.Verify(c => c.AddAsync(
            It.Is<CheckIn>(ci => ci.ProjectId == t2.ProjectId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Logs_audit_entry_for_each_changed_task()
    {
        var actorId = Guid.NewGuid();
        var t1 = ActiveTask();
        var t2 = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(t1.Id, default)).ReturnsAsync(t1);
        _tasks.Setup(r => r.GetByIdAsync(t2.Id, default)).ReturnsAsync(t2);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([t1.Id, t2.Id], "done", actorId, "127.0.0.1"), default);

        _auditLog.Verify(a => a.LogAsync("TASK_BULK_DONE", actorId, "127.0.0.1",
            It.IsAny<string>(), default), Times.Exactly(2));
    }

    [Fact]
    public async Task Bulk_done_on_qa_task_cascades_to_accept_parent()
    {
        var actorId = Guid.NewGuid();
        var (parent, qaTask) = InQaTaskWithOpenQaSubtask();
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(
            new BulkStatusChangeCommand([qaTask.Id], "done", actorId, null), default);

        result.Data.Should().Be(1);
        qaTask.Status.Should().Be(DomainTaskStatus.Done);
        parent.Status.Should().Be(DomainTaskStatus.Done);
        parent.ActualEndDate.Should().NotBeNull();
        _auditLog.Verify(a => a.LogAsync("QA_ACCEPTED", actorId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Bulk_done_on_qa_task_does_not_cascade_when_parent_already_done()
    {
        var (parent, qaTask) = InQaTaskWithOpenQaSubtask();
        parent.AcceptQa(Guid.NewGuid()); // already accepted through some other path
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        await CreateHandler().Handle(
            new BulkStatusChangeCommand([qaTask.Id], "done", Guid.NewGuid(), null), default);

        _auditLog.Verify(a => a.LogAsync("QA_ACCEPTED", It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), default), Times.Never);
    }
}
