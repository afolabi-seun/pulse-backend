using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class SetTaskProgressHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private CheckIn? _todaysCheckIn;

    public SetTaskProgressHandlerTests()
    {
        _subtasks.Setup(s => s.GetByTaskIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync(new List<Subtask>());
        _checkIns.Setup(c => c.GetByEngineerAndDateAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<Guid?>(), default))
            .ReturnsAsync(() => _todaysCheckIn);
        _checkIns.Setup(c => c.AddAsync(It.IsAny<CheckIn>(), default)).Callback<CheckIn, CancellationToken>((c, _) => _todaysCheckIn = c).Returns(Task.CompletedTask);
    }

    private SetTaskProgressHandler CreateHandler() => new(_tasks.Object, _subtasks.Object, _access.Object, _realtime.Object, _audit.Object, _checkIns.Object);

    private (PulseTask Task, Guid AssigneeId) SetUp()
    {
        var assigneeId = Guid.NewGuid();
        var task = PulseTask.Create("Fix login", 8, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(assigneeId, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        return (task, assigneeId);
    }

    [Fact]
    public async Task Moving_your_own_task_forward_checks_you_in_for_the_day()
    {
        var (task, assigneeId) = SetUp();

        var result = await CreateHandler().Handle(new SetTaskProgressCommand(task.Id, 50, assigneeId, Roles.Engineer, null), default);

        result.IsSuccess.Should().BeTrue();
        _todaysCheckIn!.EngineerId.Should().Be(assigneeId);
        _todaysCheckIn.ProjectId.Should().Be(task.ProjectId);
        _todaysCheckIn.Completed.Should().Be("Progress: Fix login — 50%");
    }

    [Fact]
    public async Task A_day_of_steps_reads_as_one_line_ending_where_progress_ended()
    {
        var (task, assigneeId) = SetUp();

        foreach (var step in new[] { 25, 50, 75 })
            await CreateHandler().Handle(new SetTaskProgressCommand(task.Id, step, assigneeId, Roles.Engineer, null), default);

        _todaysCheckIn!.Completed.Should().Be("Progress: Fix login — 75%");
        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), default), Times.Once);
    }

    [Fact]
    public async Task Moving_progress_back_adds_nothing()
    {
        var (task, assigneeId) = SetUp();
        task.SetProgress(75, assigneeId);

        var result = await CreateHandler().Handle(new SetTaskProgressCommand(task.Id, 25, assigneeId, Roles.Engineer, null), default);

        result.IsSuccess.Should().BeTrue();
        _todaysCheckIn.Should().BeNull();
    }

    [Fact]
    public async Task A_lead_setting_someone_elses_progress_does_not_check_the_lead_in()
    {
        var (task, _) = SetUp();
        var lead = Guid.NewGuid();
        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, lead, Roles.TeamLead, default)).ReturnsAsync(true);

        var result = await CreateHandler().Handle(new SetTaskProgressCommand(task.Id, 50, lead, Roles.TeamLead, null), default);

        result.IsSuccess.Should().BeTrue();
        _todaysCheckIn.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_change_checks_nobody_in()
    {
        var (task, assigneeId) = SetUp();

        var result = await CreateHandler().Handle(new SetTaskProgressCommand(task.Id, 100, assigneeId, Roles.Engineer, null), default);

        result.IsSuccess.Should().BeFalse();
        _todaysCheckIn.Should().BeNull();
    }
}
