using Pulse.Application.Common;
using FluentAssertions;

namespace Pulse.UnitTests.Common;

public class WeekOfTests
{
    // 2026-08-31 is a Monday, so 2026-08-31..2026-09-06 is one full Monday-first week.
    [Theory]
    [InlineData("2026-08-31", "2026-08-31")] // Monday itself
    [InlineData("2026-09-01", "2026-08-31")] // Tuesday
    [InlineData("2026-09-02", "2026-08-31")] // Wednesday
    [InlineData("2026-09-03", "2026-08-31")] // Thursday
    [InlineData("2026-09-04", "2026-08-31")] // Friday
    [InlineData("2026-09-05", "2026-08-31")] // Saturday
    [InlineData("2026-09-06", "2026-08-31")] // Sunday — the case the old formula got wrong
    public void Monday_returns_the_Monday_that_started_this_date_s_week(string dateStr, string expectedMondayStr)
    {
        var date = DateOnly.Parse(dateStr);
        var expected = DateOnly.Parse(expectedMondayStr);

        WeekOf.Monday(date).Should().Be(expected);
    }
}
