using Pulse.Domain.Sprints;
using FluentAssertions;

namespace Pulse.UnitTests.Sprints;

public class SprintRetrospectiveTests
{
    private static SprintRetrospective NewRetro(
        string wentWell = "Tests passed",
        string needsImprovement = "Deploy took too long",
        string actionItems = "Automate deploy") =>
        SprintRetrospective.Create(Guid.NewGuid(), Guid.NewGuid(), wentWell, needsImprovement, actionItems);

    [Fact]
    public void Create_stores_all_three_fields()
    {
        var sprintId = Guid.NewGuid();
        var retro = SprintRetrospective.Create(sprintId, Guid.NewGuid(),
            "went well", "needs work", "action items");

        retro.SprintId.Should().Be(sprintId);
        retro.WentWell.Should().Be("went well");
        retro.NeedsImprovement.Should().Be("needs work");
        retro.ActionItems.Should().Be("action items");
    }

    [Fact]
    public void Create_sets_CreatedAt_close_to_now()
    {
        var before = DateTime.UtcNow;
        var retro = NewRetro();
        var after = DateTime.UtcNow;

        retro.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Create_leaves_UpdatedAt_null()
    {
        var retro = NewRetro();

        retro.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Update_changes_all_fields()
    {
        var retro = NewRetro();

        retro.Update("new went well", "new needs", "new actions");

        retro.WentWell.Should().Be("new went well");
        retro.NeedsImprovement.Should().Be("new needs");
        retro.ActionItems.Should().Be("new actions");
    }

    [Fact]
    public void Update_sets_UpdatedAt()
    {
        var retro = NewRetro();
        var before = DateTime.UtcNow;

        retro.Update("a", "b", "c");

        retro.UpdatedAt.Should().NotBeNull();
        retro.UpdatedAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Update_preserves_SprintId_and_CreatedAt()
    {
        var retro = NewRetro();
        var originalSprintId = retro.SprintId;
        var originalCreatedAt = retro.CreatedAt;

        retro.Update("a", "b", "c");

        retro.SprintId.Should().Be(originalSprintId);
        retro.CreatedAt.Should().Be(originalCreatedAt);
    }
}
