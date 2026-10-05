using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class StartTimerCommandTests
{
    private readonly Mock<IActiveTimerRepository> _timers = new();
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private StartTimerHandler CreateHandler() =>
        new(_timers.Object, _timeEntries.Object, _tasks.Object, _subtasks.Object, _access.Object, _audit.Object);

    private void NoExistingTimer(Guid engineerId) =>
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync((ActiveTimer?)null);

    [Fact]
    public async Task Starting_a_meeting_timer_does_not_touch_the_task_repository()
    {
        var engineerId = Guid.NewGuid();
        NoExistingTimer(engineerId);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Meeting, null, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Category.Should().Be("meeting");
        _tasks.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_TIMER_STARTED", engineerId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Starting_a_new_timer_stops_and_logs_any_timer_already_running()
    {
        var engineerId = Guid.NewGuid();
        var running = ActiveTimer.Start(engineerId, TimeEntryCategory.Meeting, null);
        _timers.Setup(r => r.GetByEngineerAsync(engineerId, default)).ReturnsAsync(running);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Meeting, null, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        _timeEntries.Verify(r => r.AddAsync(It.Is<TimeEntry>(e => e.EngineerId == engineerId), default), Times.Once);
        _timers.Verify(r => r.DeleteAsync(running, default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_TIMER_STOPPED", It.IsAny<Guid>(), null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Fails_with_not_found_when_the_task_does_not_exist()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_actor_cannot_access_the_tasks_project()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(false);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_task_is_assigned_to_someone_else()
    {
        var engineerId = Guid.NewGuid();
        var otherEngineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(otherEngineerId, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Never);
    }

    [Fact]
    public async Task Starting_on_an_unassigned_task_self_assigns_the_caller()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        NoExistingTimer(engineerId);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(engineerId);
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Active);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Succeeds_for_an_ungroomed_Backlog_task_but_leaves_it_in_Backlog()
    {
        // Points no longer gate starting a timer — only auto-promotion out of Backlog still
        // requires them (PromoteFromBacklogIfGroomed), so this succeeds and a timer starts, but
        // the task's status doesn't change.
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        NoExistingTimer(engineerId);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog);
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Once);
    }

    [Fact]
    public async Task Supplying_points_on_retry_sets_them_and_proceeds()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid()); // already the caller's, still ungroomed so still Backlog
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        NoExistingTimer(engineerId);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, 5, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(5);
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Active);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_for_a_Done_task()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        task.MarkDone(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_for_a_Paused_task()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        task.Pause("stepping away", Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("paused task");
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_for_a_Blocked_task()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        task.FlagBlocker("waiting on design", Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("blocked task");
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_validation_error_when_a_subtask_is_given_for_a_meeting_timer()
    {
        var engineerId = Guid.NewGuid();

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Meeting, null, null, engineerId, null, Guid.NewGuid());
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Fails_with_not_found_when_the_subtask_does_not_belong_to_the_given_task()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        var subtask = Subtask.Create(Guid.NewGuid(), "Checklist item", Guid.NewGuid()); // different TaskId
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null, subtask.Id);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_subtask_is_loaned_to_someone_else()
    {
        var engineerId = Guid.NewGuid();
        var otherEngineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        var subtask = Subtask.Create(taskId, "Checklist item", Guid.NewGuid());
        subtask.Loan(otherEngineerId);
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null, subtask.Id);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _timers.Verify(r => r.AddAsync(It.IsAny<ActiveTimer>(), default), Times.Never);
    }

    [Fact]
    public async Task A_subtask_loaned_to_the_caller_overrides_the_parent_tasks_own_assignee()
    {
        // The whole point of loaning a checklist item to someone other than the task's assignee —
        // that engineer must be able to time their piece without the task-level ownership guard
        // (which would otherwise reject them as "someone else's task").
        var engineerId = Guid.NewGuid();
        var taskOwnerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(taskOwnerId, Guid.NewGuid());
        var subtask = Subtask.Create(taskId, "Checklist item", Guid.NewGuid());
        subtask.Loan(engineerId);
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessTaskAsync(taskId, engineerId, Roles.Engineer, default)).ReturnsAsync(true);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);
        NoExistingTimer(engineerId);

        var cmd = new StartTimerCommand(engineerId, Roles.Engineer, TimeEntryCategory.Task, taskId, null, engineerId, null, subtask.Id);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.SubtaskId.Should().Be(subtask.Id);
        result.Data.SubtaskTitle.Should().Be("Checklist item");
        task.AssigneeId.Should().Be(taskOwnerId); // unchanged — loaning a subtask never reassigns the task
        _timers.Verify(r => r.AddAsync(It.Is<ActiveTimer>(t => t.SubtaskId == subtask.Id), default), Times.Once);
    }
}
