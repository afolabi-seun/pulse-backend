using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
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

    private ToggleSubtaskHandler CreateHandler() =>
        new(_tasks.Object, _subtasks.Object, _audit.Object);

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
}
