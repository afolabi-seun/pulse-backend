using Pulse.Application.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Tasks;

/// <summary>The import CSV used to parse due dates with the server's culture: on an invariant-culture server "16/10/2026" failed and "09/10/2026" became 10 September.</summary>
public class ImportDateParserTests
{
    [Theory]
    [InlineData("2026-10-16", 2026, 10, 16)]
    [InlineData("2026/10/16", 2026, 10, 16)]
    [InlineData("16/10/2026", 2026, 10, 16)]
    [InlineData("9/10/2026", 2026, 10, 9)]
    [InlineData("16-10-2026", 2026, 10, 16)]
    [InlineData("16.10.2026", 2026, 10, 16)]
    [InlineData("16 Oct 2026", 2026, 10, 16)]
    [InlineData("16 October 2026", 2026, 10, 16)]
    [InlineData("16-Oct-2026", 2026, 10, 16)]
    [InlineData("  16/10/2026  ", 2026, 10, 16)]
    public void Reads_the_formats_people_actually_type(string text, int y, int m, int d)
    {
        ImportDateParser.TryParse(text, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(y, m, d));
    }

    [Fact]
    public void A_slash_date_is_day_first_never_month_first()
    {
        // 09/10/2026 is 9 October. Month-first (the server's culture) read it as 10 September, silently.
        ImportDateParser.TryParse("09/10/2026", out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(2026, 10, 9));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("31/02/2026")]
    [InlineData("10/16/2026")]   // month-first with an impossible month: refused, not reinterpreted
    [InlineData("16/10/26")]
    public void Refuses_what_it_cannot_read_unambiguously(string? text)
    {
        ImportDateParser.TryParse(text, out _).Should().BeFalse();
    }
}
