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

    // ── Hours are kept to two decimals ────────────────────────────────────────

    private static TimeEntry LogMeeting(decimal hours) =>
        TimeEntry.Log(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), TimeEntryCategory.Meeting, null, null, hours, null);

    [Theory]
    [InlineData(3.4200000000000004, 3.42)]   // what a floating-point client sends
    [InlineData(1.005, 1.01)]                // half rounds up, not to even
    [InlineData(2.675, 2.68)]
    [InlineData(0.125, 0.13)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.01, 0.01)]
    public void Log_rounds_hours_to_two_decimals_so_the_value_matches_what_the_database_will_hold(double given, double expected)
    {
        LogMeeting((decimal)given).Hours.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(0.004)]
    [InlineData(0.0049)]
    public void Log_refuses_a_sliver_that_would_round_to_zero_instead_of_storing_a_zero_hour_entry(double given)
    {
        var act = () => LogMeeting((decimal)given);

        act.Should().Throw<DomainException>().WithMessage("*greater than zero*");
    }

    [Fact]
    public void Log_still_refuses_more_than_24_hours_but_allows_exactly_24()
    {
        LogMeeting(24.004m).Hours.Should().Be(24m);      // rounds to 24.00
        var act = () => LogMeeting(24.01m);
        act.Should().Throw<DomainException>().WithMessage("*24 hours*");
    }

    [Fact]
    public void Update_rounds_hours_the_same_way()
    {
        var entry = LogMeeting(1m);

        entry.Update(entry.Date, TimeEntryCategory.Meeting, null, null, 3.4200000000000004m, null);

        entry.Hours.Should().Be(3.42m);
    }

    [Fact]
    public void Update_refuses_a_sliver_and_leaves_the_entry_unchanged()
    {
        var entry = LogMeeting(2m);

        var act = () => entry.Update(entry.Date, TimeEntryCategory.Meeting, null, null, 0.004m, null);

        act.Should().Throw<DomainException>();
        entry.Hours.Should().Be(2m);
    }
}
