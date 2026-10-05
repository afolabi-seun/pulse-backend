using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class AssigneeEditTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISprintRepository> _sprints = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private AssigneeEditTaskHandler CreateHandler() => new(_tasks.Object, _sprints.Object, _audit.Object);

    private static PulseTask AssignedTask(Guid assigneeId)
    {
        var task = PulseTask.Create("A task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(assigneeId, Guid.NewGuid());
        return task;
    }

    [Fact]
    public async Task Assignee_can_edit_their_own_tasks_description_and_due_date()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var newDueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, "Updated description", newDueDate, assigneeId, null, "Waiting on API access"), default);

        result.IsSuccess.Should().BeTrue();
        task.Description.Should().Be("Updated description");
        task.DueDate.Should().Be(newDueDate);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _audit.Verify(a => a.LogAsync("TASK_ASSIGNEE_EDITED", assigneeId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Someone_other_than_the_assignee_is_forbidden()
    {
        var assigneeId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, "Nope", null, otherId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.Description.Should().BeNull();
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Points_and_title_cannot_be_touched_through_this_path()
    {
        // The command doesn't even accept Points/Title — this just confirms editing description
        // never disturbs the task's existing points, the real guardrail this endpoint exists for.
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, "New description", null, assigneeId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(3);
        task.Title.Should().Be("A task");
    }

    [Fact]
    public async Task Due_date_cannot_exceed_the_tasks_sprint_end_date()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        var sprint = Sprint.Create(Guid.NewGuid(), null, "Sprint 1",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)));
        task.AssignToSprint(sprint.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(sprint.Id, default)).ReturnsAsync(sprint);
        var tooLate = sprint.EndDate.AddDays(1);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, null, tooLate, assigneeId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Due_date_within_the_sprint_is_accepted()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        var sprint = Sprint.Create(Guid.NewGuid(), null, "Sprint 1",
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)));
        task.AssignToSprint(sprint.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(sprint.Id, default)).ReturnsAsync(sprint);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, null, sprint.EndDate, assigneeId, null, "Sprint re-planned"), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(sprint.EndDate);
    }

    [Fact]
    public async Task No_sprint_means_no_upper_bound_on_the_due_date()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var farFuture = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, null, farFuture, assigneeId, null, "Blocked by vendor"), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(farFuture);
    }

    [Fact]
    public async Task Moving_an_existing_due_date_requires_a_reason()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        var original = task.DueDate;
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        foreach (var reason in new string?[] { null, "", "   " })
        {
            var result = await CreateHandler().Handle(
                new AssigneeEditTaskCommand(task.Id, null, original!.Value.AddDays(3), assigneeId, null, reason), default);

            result.IsSuccess.Should().BeFalse();
            result.ErrorCode.Should().Be("VALIDATION_ERROR");
        }

        task.DueDate.Should().Be(original);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task The_stated_reason_is_trimmed_and_recorded_on_the_due_date_history_entry()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        var original = task.DueDate!.Value;
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, null, original.AddDays(3), assigneeId, null, "  Design review slipped  "), default);

        result.IsSuccess.Should().BeTrue();
        var entry = task.History.Single(h => h.Field == "due_date");
        entry.Reason.Should().Be("Design review slipped");
        entry.OldValue.Should().Be(original.ToString("O"));
    }

    [Fact]
    public async Task Setting_a_first_due_date_does_not_require_a_reason()
    {
        var assigneeId = Guid.NewGuid();
        var task = PulseTask.Create("Unscheduled", 3, Guid.NewGuid());
        task.Assign(assigneeId, Guid.NewGuid());
        task.DueDate.Should().BeNull();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, null, due, assigneeId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(due);
    }

    [Fact]
    public async Task An_unchanged_due_date_does_not_require_a_reason()
    {
        var assigneeId = Guid.NewGuid();
        var task = AssignedTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(task.Id, "Just the description", task.DueDate, assigneeId, null), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_task_returns_not_found()
    {
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new AssigneeEditTaskCommand(taskId, "x", null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }
}
