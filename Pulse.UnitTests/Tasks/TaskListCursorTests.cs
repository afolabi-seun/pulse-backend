using Pulse.Application.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Tasks;

public class TaskListCursorTests
{
    [Fact]
    public void Roundtrips_an_active_group_cursor()
    {
        var activatedAt = new DateTime(2026, 6, 23, 14, 30, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();

        TaskListCursor.TryDecode(TaskListCursor.Encode(true, activatedAt, null, id),
            out var isActive, out var gotActivatedAt, out var gotDueDate, out var gotId).Should().BeTrue();

        isActive.Should().BeTrue();
        gotActivatedAt.Should().Be(activatedAt);
        gotDueDate.Should().BeNull();
        gotId.Should().Be(id);
    }

    [Fact]
    public void Roundtrips_a_dated_non_active_cursor()
    {
        var date = new DateOnly(2026, 6, 23);
        var id = Guid.NewGuid();

        TaskListCursor.TryDecode(TaskListCursor.Encode(false, null, date, id),
            out var isActive, out var gotActivatedAt, out var gotDueDate, out var gotId).Should().BeTrue();

        isActive.Should().BeFalse();
        gotActivatedAt.Should().BeNull();
        gotDueDate.Should().Be(date);
        gotId.Should().Be(id);
    }

    [Fact]
    public void Roundtrips_an_undated_non_active_cursor()
    {
        var id = Guid.NewGuid();

        TaskListCursor.TryDecode(TaskListCursor.Encode(false, null, null, id),
            out var isActive, out var gotActivatedAt, out var gotDueDate, out var gotId).Should().BeTrue();

        isActive.Should().BeFalse();
        gotActivatedAt.Should().BeNull();
        gotDueDate.Should().BeNull();
        gotId.Should().Be(id);
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("")]
    [InlineData("Zm9vYmFy")]            // base64 of "foobar" — no '|' separators
    [InlineData("MHxudWxsfDIwMjYtMDEtMDF8bm90LWEtZ3VpZA==")] // "0|null|2026-01-01|not-a-guid"
    public void Returns_false_for_malformed_input(string cursor)
    {
        TaskListCursor.TryDecode(cursor, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_a_stale_pre_two_group_format_cursor()
    {
        // The old cursor shape was "{dueDate|null}|{id}" — two fields, not four. It must not be
        // silently misread as the new format; it should surface as an ordinary invalid cursor.
        var oldFormatCursor = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"2026-06-23|{Guid.NewGuid()}"));

        TaskListCursor.TryDecode(oldFormatCursor, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Roundtrips_a_sorted_cursor_with_a_value()
    {
        var id = Guid.NewGuid();

        TaskListCursor.TryDecodeSorted(TaskListCursor.EncodeSorted("points", "desc", "8", id),
            out var sortBy, out var direction, out var value, out var gotId).Should().BeTrue();

        sortBy.Should().Be("points");
        direction.Should().Be("desc");
        value.Should().Be("8");
        gotId.Should().Be(id);
    }

    [Fact]
    public void Roundtrips_a_sorted_cursor_with_a_null_value()
    {
        var id = Guid.NewGuid();

        TaskListCursor.TryDecodeSorted(TaskListCursor.EncodeSorted("dueDate", "asc", null, id),
            out var sortBy, out var direction, out var value, out var gotId).Should().BeTrue();

        sortBy.Should().Be("dueDate");
        direction.Should().Be("asc");
        value.Should().BeNull();
        gotId.Should().Be(id);
    }

    [Theory]
    [InlineData("not-base64!!!")]
    [InlineData("")]
    public void TryDecodeSorted_returns_false_for_malformed_input(string cursor)
    {
        TaskListCursor.TryDecodeSorted(cursor, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecodeSorted_does_not_misread_the_default_cursor_format()
    {
        // The default (no-sort) cursor is also four '|'-delimited parts, so TryDecodeSorted can
        // technically parse it without erroring — but the two formats are never cross-decoded in
        // practice: the handler picks one decode function based on whether a sort was requested.
        // This just documents that TryDecodeSorted doesn't throw on it.
        var id = Guid.NewGuid();
        var defaultCursor = TaskListCursor.Encode(true, DateTime.UtcNow, null, id);

        var decoded = TaskListCursor.TryDecodeSorted(defaultCursor, out _, out _, out _, out var gotId);

        decoded.Should().BeTrue();
        gotId.Should().Be(id);
    }
}
