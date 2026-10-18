using Pulse.Application.Common;
using FluentAssertions;

namespace Pulse.UnitTests.Common;

public class WorkingHoursTests
{
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc); // 14 Oct 2026 is a Wednesday

    private static TimeSpan Worked(DateTime from, DateTime to) => WorkingHours.Between(from, to, 8, 18);

    [Fact]
    public void Time_inside_the_working_day_counts_in_full() =>
        Worked(At(14, 10), At(14, 11, 30)).Should().Be(TimeSpan.FromMinutes(90));

    [Fact]
    public void Time_before_opening_and_after_closing_does_not_count()
    {
        Worked(At(14, 6), At(14, 7, 59)).Should().Be(TimeSpan.Zero);
        Worked(At(14, 18), At(14, 23)).Should().Be(TimeSpan.Zero);
        Worked(At(14, 7), At(14, 9)).Should().Be(TimeSpan.FromHours(1), "only 08:00–09:00 is working time");
    }

    [Fact]
    public void Overnight_only_the_two_working_slices_count() =>
        Worked(At(14, 17, 30), At(15, 8, 30)).Should().Be(TimeSpan.FromHours(1));

    [Fact]
    public void A_weekend_does_not_count_at_all()
    {
        // Friday 17:00 to Monday 09:00: one hour on Friday and one on Monday.
        Worked(At(16, 17), At(19, 9)).Should().Be(TimeSpan.FromHours(2));
        Worked(At(17, 9), At(18, 17)).Should().Be(TimeSpan.Zero, "Saturday and Sunday");
    }

    [Fact]
    public void A_whole_working_week_is_fifty_hours() =>
        Worked(At(12, 0), At(19, 0)).Should().Be(TimeSpan.FromHours(50)); // Mon 12 to Mon 19, five days of ten hours

    [Fact]
    public void Nothing_elapsed_or_an_end_before_the_start_is_zero()
    {
        Worked(At(14, 10), At(14, 10)).Should().Be(TimeSpan.Zero);
        Worked(At(14, 12), At(14, 10)).Should().Be(TimeSpan.Zero);
    }
}
