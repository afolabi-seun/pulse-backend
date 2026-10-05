using Pulse.Domain.Common;
using Pulse.Domain.TimeEntries;
using FluentAssertions;

namespace Pulse.UnitTests.TimeEntries;

public class ActiveTimerTests
{
    [Fact]
    public void Start_throws_for_a_non_Task_non_Meeting_category()
    {
        var act = () => ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Admin, null);
        act.Should().Throw<DomainException>().WithMessage("*Task or a Meeting*");
    }

    [Fact]
    public void Start_throws_when_Task_category_has_no_TaskId()
    {
        var act = () => ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Task, null);
        act.Should().Throw<DomainException>().WithMessage("*requires a TaskId*");
    }

    [Fact]
    public void Start_throws_when_Meeting_category_references_a_task()
    {
        var act = () => ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Meeting, Guid.NewGuid());
        act.Should().Throw<DomainException>().WithMessage("*cannot reference a task*");
    }

    [Fact]
    public void Start_succeeds_for_a_Task_category_with_a_TaskId()
    {
        var engineerId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var timer = ActiveTimer.Start(engineerId, TimeEntryCategory.Task, taskId);

        timer.EngineerId.Should().Be(engineerId);
        timer.Category.Should().Be(TimeEntryCategory.Task);
        timer.TaskId.Should().Be(taskId);
        timer.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Start_succeeds_for_a_Meeting_category_with_no_TaskId()
    {
        var timer = ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Meeting, null);

        timer.Category.Should().Be(TimeEntryCategory.Meeting);
        timer.TaskId.Should().BeNull();
    }

    [Fact]
    public void Start_throws_when_a_SubtaskId_is_given_with_no_TaskId()
    {
        var act = () => ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Meeting, null, Guid.NewGuid());
        act.Should().Throw<DomainException>().WithMessage("*subtask timer requires a TaskId*");
    }

    [Fact]
    public void Start_succeeds_with_a_SubtaskId_alongside_a_TaskId()
    {
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();

        var timer = ActiveTimer.Start(Guid.NewGuid(), TimeEntryCategory.Task, taskId, subtaskId);

        timer.TaskId.Should().Be(taskId);
        timer.SubtaskId.Should().Be(subtaskId);
    }
}
