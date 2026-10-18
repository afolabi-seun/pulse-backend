using Pulse.Application.Escalations;
using FluentAssertions;

namespace Pulse.UnitTests.Escalations;

public class DueDateWordingTests
{
    // Wednesday 14 Oct 2026, mid-morning UTC.
    private static readonly DateTime Now = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2026-10-14", "today")]
    [InlineData("2026-10-15", "tomorrow")]
    [InlineData("2026-10-16", "in 2 days")]
    [InlineData("2026-10-17", "in 3 days")]
    [InlineData("2026-10-24", "in 10 days")]
    [InlineData("2026-10-13", "yesterday")]
    [InlineData("2026-10-11", "3 days ago")]
    public void Says_how_far_away_the_date_really_is(string due, string expected)
    {
        DueDateWording.When(DateOnly.Parse(due), Now).Should().Be(expected);
    }

    [Fact]
    public void Counts_calendar_days_so_the_time_of_day_never_changes_the_answer()
    {
        var due = new DateOnly(2026, 10, 15);

        DueDateWording.When(due, new DateTime(2026, 10, 14, 0, 0, 1, DateTimeKind.Utc)).Should().Be("tomorrow");
        DueDateWording.When(due, new DateTime(2026, 10, 14, 23, 59, 59, DateTimeKind.Utc)).Should().Be("tomorrow");
        DueDateWording.When(due, new DateTime(2026, 10, 15, 0, 0, 1, DateTimeKind.Utc)).Should().Be("today");
    }

    [Theory]
    [InlineData("2026-10-13", " (yesterday)")]
    [InlineData("2026-10-11", " (3 days ago)")]
    [InlineData("2026-10-14", "")]       // not past: nothing to add
    [InlineData("2026-10-20", "")]
    public void The_overdue_suffix_only_appears_for_a_date_that_has_passed(string due, string expected)
    {
        DueDateWording.OverdueSuffix(DateOnly.Parse(due), Now).Should().Be(expected);
    }
}
