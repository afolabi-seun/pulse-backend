using Pulse.Application.Projects.Queries;
using FluentAssertions;

namespace Pulse.UnitTests.Projects;

public class ProjectActivityCursorTests
{
    [Fact]
    public void Roundtrips_a_cursor()
    {
        var changedAt = new DateTime(2026, 6, 23, 14, 30, 5, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        ProjectActivityCursor.TryDecode(ProjectActivityCursor.Encode(changedAt, id), out var got, out var gotId).Should().BeTrue();
        got.Should().Be(changedAt);
        gotId.Should().Be(id);
    }

    [Fact]
    public void Roundtrips_two_entries_sharing_the_same_timestamp_with_different_ids()
    {
        // Same-transaction TaskHistory rows commonly share an identical ChangedAt — the cursor
        // must still distinguish them via the Id tiebreaker.
        var changedAt = new DateTime(2026, 6, 23, 14, 30, 5, DateTimeKind.Utc);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        ProjectActivityCursor.TryDecode(ProjectActivityCursor.Encode(changedAt, idA), out _, out var gotA).Should().BeTrue();
        ProjectActivityCursor.TryDecode(ProjectActivityCursor.Encode(changedAt, idB), out _, out var gotB).Should().BeTrue();
        gotA.Should().Be(idA);
        gotB.Should().Be(idB);
        gotA.Should().NotBe(gotB);
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("")]
    [InlineData("Zm9vYmFy")]            // base64 of "foobar" — no '|' separator
    [InlineData("MjAyNi0wMS0wMXxub3QtYS1ndWlk")] // "2026-01-01|not-a-guid"
    public void Returns_false_for_malformed_input(string cursor)
    {
        ProjectActivityCursor.TryDecode(cursor, out _, out _).Should().BeFalse();
    }
}
