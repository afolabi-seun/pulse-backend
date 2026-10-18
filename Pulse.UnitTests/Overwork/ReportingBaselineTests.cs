using Pulse.Application.Overwork;
using FluentAssertions;

namespace Pulse.UnitTests.Overwork;

public class ReportingBaselineTests
{
    [Fact]
    public void With_no_baseline_nothing_is_changed()
    {
        var t = new OverworkThresholds();

        t.FloorDate(new DateOnly(2026, 9, 3)).Should().Be(new DateOnly(2026, 9, 3));
        t.FloorTime(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc)).Should().Be(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_date_before_the_baseline_is_lifted_to_it_and_one_on_or_after_is_left_alone()
    {
        var t = new OverworkThresholds { ReportingBaseline = new DateOnly(2026, 10, 1) };

        t.FloorDate(new DateOnly(2026, 9, 30)).Should().Be(new DateOnly(2026, 10, 1));
        t.FloorDate(new DateOnly(2026, 10, 1)).Should().Be(new DateOnly(2026, 10, 1));
        t.FloorDate(new DateOnly(2026, 10, 9)).Should().Be(new DateOnly(2026, 10, 9));
    }

    [Fact]
    public void The_baseline_day_starts_at_local_midnight_which_is_the_evening_before_in_UTC()
    {
        var t = new OverworkThresholds { ReportingBaseline = new DateOnly(2026, 10, 1) };
        var startOfBaselineDay = new DateTime(2026, 9, 30, 23, 0, 0, DateTimeKind.Utc); // 00:00 at UTC+1

        t.FloorTime(new DateTime(2026, 9, 30, 22, 59, 0, DateTimeKind.Utc)).Should().Be(startOfBaselineDay, "a minute before local midnight is still September");
        t.FloorTime(startOfBaselineDay).Should().Be(startOfBaselineDay);
        t.FloorTime(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc)).Should().Be(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));
    }
}
