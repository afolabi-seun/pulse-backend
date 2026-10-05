using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Commands;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using Pulse.Domain.TimeEntries;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class LogTimeEntryCommandTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    private LogTimeEntryHandler CreateHandler() => new(_timeEntries.Object, _tasks.Object, _projects.Object, _audit.Object);

    [Fact]
    public async Task Logs_a_valid_task_entry()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid()); // Active — Backlog is rejected by EnsureCanLogTimeEntry
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2, "notes", engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Hours.Should().Be(2);
        result.Data!.Category.Should().Be("task");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Once);
        _audit.Verify(a => a.LogAsync("TIME_ENTRY_LOGGED", engineerId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_when_the_task_is_paused()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        task.Pause("stepping away", Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("paused task");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_when_the_task_is_blocked()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        task.Assign(engineerId, Guid.NewGuid());
        task.FlagBlocker("waiting on design", Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("blocked task");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_when_the_task_is_in_backlog()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        // Unassigned PulseTask.Create defaults to Backlog — unlike ActivateForTimer (the timer's
        // "Unclaimed" tab deliberately allows this), a manual entry has no claiming motion to protect.
        var task = PulseTask.Create("Some task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync(task);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("hasn't started");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_not_found_when_the_task_does_not_exist()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_not_found_when_the_project_does_not_exist()
    {
        var engineerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        _projects.Setup(r => r.GetByIdAsync(projectId, default)).ReturnsAsync((Project?)null);

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, null, projectId, 1, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_when_the_domain_guard_rejects_it()
    {
        var engineerId = Guid.NewGuid();

        var cmd = new LogTimeEntryCommand(engineerId, DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 0, null, engineerId, null);
        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        _timeEntries.Verify(r => r.AddAsync(It.IsAny<TimeEntry>(), default), Times.Never);
    }
}
