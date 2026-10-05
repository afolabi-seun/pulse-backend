using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Tasks;

public class TaskCommentTests
{
    private static TaskComment NewComment(string body = "Hello world") =>
        TaskComment.Create(Guid.NewGuid(), Guid.NewGuid(), body);

    [Fact]
    public void Create_stores_body_trimmed()
    {
        var comment = TaskComment.Create(Guid.NewGuid(), Guid.NewGuid(), "  trimmed  ");

        comment.Body.Should().Be("trimmed");
    }

    [Fact]
    public void Create_sets_TaskId_and_AuthorId()
    {
        var taskId = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        var comment = TaskComment.Create(taskId, authorId, "body");

        comment.TaskId.Should().Be(taskId);
        comment.AuthorId.Should().Be(authorId);
    }

    [Fact]
    public void Create_sets_CreatedAt_close_to_now()
    {
        var before = DateTime.UtcNow;
        var comment = NewComment();
        var after = DateTime.UtcNow;

        comment.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Create_leaves_EditedAt_null()
    {
        var comment = NewComment();

        comment.EditedAt.Should().BeNull();
    }

    [Fact]
    public void Edit_updates_body_and_sets_EditedAt()
    {
        var before = DateTime.UtcNow;
        var comment = NewComment("original");

        comment.Edit("updated body");

        comment.Body.Should().Be("updated body");
        comment.EditedAt.Should().NotBeNull();
        comment.EditedAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Edit_trims_whitespace()
    {
        var comment = NewComment("original");

        comment.Edit("  padded  ");

        comment.Body.Should().Be("padded");
    }

    [Fact]
    public void Edit_preserves_original_CreatedAt()
    {
        var comment = NewComment();
        var originalCreatedAt = comment.CreatedAt;

        comment.Edit("changed");

        comment.CreatedAt.Should().Be(originalCreatedAt);
    }
}
