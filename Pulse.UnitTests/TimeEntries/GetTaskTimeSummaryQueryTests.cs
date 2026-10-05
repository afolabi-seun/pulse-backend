using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class GetTaskTimeSummaryQueryTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    private GetTaskTimeSummaryHandler CreateHandler() => new(_timeEntries.Object, _tasks.Object, _engineers.Object, _access.Object);

    [Fact]
    public async Task Fails_with_not_found_when_the_task_does_not_exist()
    {
        var taskId = Guid.NewGuid();
        _tasks.Setup(r => r.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(new GetTaskTimeSummaryQuery(taskId, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_actor_cannot_access_the_task()
    {
        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(new GetTaskTimeSummaryQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_null_expected_hours_when_the_task_has_no_assignee()
    {
        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);
        _timeEntries.Setup(r => r.GetTotalHoursByTaskAsync(task.Id, default)).ReturnsAsync(4m);

        var result = await CreateHandler().Handle(new GetTaskTimeSummaryQuery(task.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TotalHoursLogged.Should().Be(4m);
        result.Data!.ExpectedHours.Should().BeNull();
    }

    [Fact]
    public async Task Computes_expected_hours_from_points_and_the_assignees_baseline()
    {
        var actorId = Guid.NewGuid();
        var task = PulseTask.Create("Task", 4, Guid.NewGuid()); // 4 points
        var assignee = Engineer.Create("Assignee", "a@x.io", "hash", Roles.Engineer, 2, 1); // 2 pts / 1 day baseline
        task.Assign(assignee.Id, actorId);

        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanViewTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(true);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _timeEntries.Setup(r => r.GetTotalHoursByTaskAsync(task.Id, default)).ReturnsAsync(10m);

        var result = await CreateHandler().Handle(new GetTaskTimeSummaryQuery(task.Id, actorId, Roles.Engineer), default);

        // expectedDays = 4pts * 1 day / 2pts = 2 days; expectedHours = 2 * 8 = 16
        result.IsSuccess.Should().BeTrue();
        result.Data!.ExpectedHours.Should().Be(16m);
        result.Data!.TotalHoursLogged.Should().Be(10m);
    }
}
