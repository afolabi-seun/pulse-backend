using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class ToggleSubtaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private CheckIn? _todaysCheckIn;

    public ToggleSubtaskHandlerTests()
    {
        // A tiny in-memory stand-in for today's check-in, so a second tick can find and update the first one's line.
        _checkIns.Setup(c => c.GetByEngineerAndDateAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<Guid?>(), default))
            .ReturnsAsync(() => _todaysCheckIn);
        _checkIns.Setup(c => c.AddAsync(It.IsAny<CheckIn>(), default)).Callback<CheckIn, CancellationToken>((c, _) => _todaysCheckIn = c).Returns(Task.CompletedTask);
    }

    private ToggleSubtaskHandler CreateHandler() =>
        new(_tasks.Object, _subtasks.Object, _audit.Object, _checkIns.Object);

    private (PulseTask Task, Subtask Subtask, Guid AssigneeId) SetUp(bool subtaskAlreadyDone = false)
    {
        var assigneeId = Guid.NewGuid();
        var task = PulseTask.Create("Task", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(assigneeId, Guid.NewGuid());

        var subtask = Subtask.Create(task.Id, "A checklist item", assigneeId);
        if (subtaskAlreadyDone)
            subtask.SetDone(true, assigneeId);

        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);
        _subtasks.Setup(r => r.GetByTaskIdAsync(task.Id, default)).ReturnsAsync(new List<Subtask> { subtask });

        return (task, subtask, assigneeId);
    }

    [Fact]
    public async Task Completing_a_subtask_records_history_and_an_audit_log_entry()
    {
        var (task, subtask, assigneeId) = SetUp();

        var result = await CreateHandler().Handle(
            new ToggleSubtaskCommand(subtask.Id, true, assigneeId, Roles.Engineer, "1.2.3.4"), default);

        result.IsSuccess.Should().BeTrue();
        task.History.Should().ContainSingle(h => h.Field == "subtask_completed" && h.NewValue == subtask.Title);
        _audit.Verify(a => a.LogAsync("SUBTASK_COMPLETED", assigneeId, "1.2.3.4", It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Unchecking_a_subtask_records_neither_history_nor_an_audit_log_entry()
    {
        var (task, subtask, assigneeId) = SetUp(subtaskAlreadyDone: true);

        var result = await CreateHandler().Handle(
            new ToggleSubtaskCommand(subtask.Id, false, assigneeId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.History.Should().NotContain(h => h.Field == "subtask_completed");
        _audit.Verify(a => a.LogAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Re_toggling_an_already_done_subtask_to_done_again_does_not_duplicate_history_or_audit()
    {
        var (task, subtask, assigneeId) = SetUp(subtaskAlreadyDone: true);

        var result = await CreateHandler().Handle(
            new ToggleSubtaskCommand(subtask.Id, true, assigneeId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.History.Should().NotContain(h => h.Field == "subtask_completed", "it was already done — this isn't a new completion event");
        _audit.Verify(a => a.LogAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), default), Times.Never);
    }

    // ── automatic check-in ───────────────────────────────────────────────────

    [Fact]
    public async Task Ticking_off_an_item_on_your_own_task_checks_you_in_for_the_day()
    {
        var (task, subtask, assigneeId) = SetUp();

        await CreateHandler().Handle(new ToggleSubtaskCommand(subtask.Id, true, assigneeId, Roles.Engineer), default);

        _todaysCheckIn.Should().NotBeNull();
        _todaysCheckIn!.EngineerId.Should().Be(assigneeId);
        _todaysCheckIn.ProjectId.Should().Be(task.ProjectId);
        _todaysCheckIn.Completed.Should().Be("Checklist: Task — 1 of 1 items done");
    }

    [Fact]
    public async Task Several_ticks_the_same_day_update_one_line_instead_of_adding_a_line_each()
    {
        var (task, first, assigneeId) = SetUp();
        var second = Subtask.Create(task.Id, "Another item", assigneeId);
        var third = Subtask.Create(task.Id, "A third item", assigneeId);
        var all = new List<Subtask> { first, second, third };
        _subtasks.Setup(r => r.GetByTaskIdAsync(task.Id, default)).ReturnsAsync(all);
        _subtasks.Setup(r => r.GetByIdAsync(second.Id, default)).ReturnsAsync(second);

        await CreateHandler().Handle(new ToggleSubtaskCommand(first.Id, true, assigneeId, Roles.Engineer), default);
        await CreateHandler().Handle(new ToggleSubtaskCommand(second.Id, true, assigneeId, Roles.Engineer), default);

        _todaysCheckIn!.Completed.Should().Be("Checklist: Task — 2 of 3 items done");
        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), default), Times.Once, "the second tick updates the first one's check-in");
    }

    [Fact]
    public async Task Unticking_an_item_adds_nothing()
    {
        var (_, subtask, assigneeId) = SetUp(subtaskAlreadyDone: true);

        await CreateHandler().Handle(new ToggleSubtaskCommand(subtask.Id, false, assigneeId, Roles.Engineer), default);

        _todaysCheckIn.Should().BeNull();
    }

    [Fact]
    public async Task A_lead_ticking_off_someone_elses_item_does_not_check_the_lead_in()
    {
        var (_, subtask, _) = SetUp();
        var lead = Guid.NewGuid();

        var result = await CreateHandler().Handle(new ToggleSubtaskCommand(subtask.Id, true, lead, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        _todaysCheckIn.Should().BeNull();
    }
}
