using Pulse.Domain.Common;
using Pulse.Domain.TimeEntries;
using FluentAssertions;

namespace Pulse.UnitTests.TimeEntries;

public class TimeEntryDomainTests
{
    [Fact]
    public void Log_throws_when_Task_category_has_no_TaskId()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, null, null, 2, null);
        act.Should().Throw<DomainException>().WithMessage("*requires a TaskId*");
    }

    [Fact]
    public void Log_throws_when_a_non_Task_category_references_a_task()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, Guid.NewGuid(), null, 1, null);
        act.Should().Throw<DomainException>().WithMessage("*may reference a task*");
    }

    [Fact]
    public void Log_throws_when_a_Task_category_entry_also_sets_ProjectId()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, Guid.NewGuid(), Guid.NewGuid(), 1, null);
        act.Should().Throw<DomainException>().WithMessage("*derived from its task*");
    }

    [Fact]
    public void Log_succeeds_for_a_non_task_entry_with_an_explicit_ProjectId()
    {
        var projectId = Guid.NewGuid();
        var entry = TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, null, projectId, 1, null);

        entry.ProjectId.Should().Be(projectId);
    }

    [Fact]
    public void Log_throws_when_hours_is_zero_or_negative()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 0, null);
        act.Should().Throw<DomainException>().WithMessage("*greater than zero*");
    }

    [Fact]
    public void Log_throws_when_hours_exceeds_24()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 24.5m, null);
        act.Should().Throw<DomainException>().WithMessage("*cannot exceed 24 hours*");
    }

    [Fact]
    public void Log_succeeds_for_a_valid_task_entry()
    {
        var taskId = Guid.NewGuid();
        var entry = TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 2.5m, "Worked on it");

        entry.TaskId.Should().Be(taskId);
        entry.Hours.Should().Be(2.5m);
        entry.Category.Should().Be(TimeEntryCategory.Task);
    }

    [Fact]
    public void Log_succeeds_for_a_valid_non_task_entry()
    {
        var entry = TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, null, null, 1, "Sprint planning");

        entry.TaskId.Should().BeNull();
        entry.Category.Should().Be(TimeEntryCategory.Meeting);
    }

    [Fact]
    public void Update_re_validates_and_applies_the_same_guard_clauses()
    {
        var entry = TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Admin, null, null, 1, null);

        var act = () => entry.Update(DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, null, null, 1, null);
        act.Should().Throw<DomainException>().WithMessage("*requires a TaskId*");
    }

    [Fact]
    public void Log_throws_when_a_SubtaskId_is_given_with_no_TaskId()
    {
        var act = () => TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, null, null, 1, null, Guid.NewGuid());
        act.Should().Throw<DomainException>().WithMessage("*subtask reference requires a TaskId*");
    }

    [Fact]
    public void Log_succeeds_with_a_SubtaskId_alongside_a_TaskId()
    {
        var taskId = Guid.NewGuid();
        var subtaskId = Guid.NewGuid();

        var entry = TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Task, taskId, null, 1, null, subtaskId);

        entry.SubtaskId.Should().Be(subtaskId);
    }
}
