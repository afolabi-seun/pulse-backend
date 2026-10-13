using Pulse.Application.Comments.Commands;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Comments;

public class AddCommentHandlerTests
{
    private readonly Mock<ITaskCommentRepository> _comments = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public AddCommentHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _access
            .Setup(a => a.GetAccessibleEngineerIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
    }

    private AddCommentHandler CreateHandler() =>
        new(_comments.Object, _engineers.Object, _access.Object, _tasks.Object, _projects.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object);

    private (PulseTask Task, Engineer Author) SeedTaskAndAuthor()
    {
        var task = PulseTask.Create("Rebuild pipeline", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        var author = Engineer.Create("Priya PM", "priya@test.io", "hash", Roles.ProjectManager, 20, 14);
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(author.Id, default)).ReturnsAsync(author);
        return (task, author);
    }

    [Fact]
    public async Task Handle_returns_EMPTY_BODY_for_blank_comment()
    {
        var result = await CreateHandler().Handle(new AddCommentCommand(Guid.NewGuid(), Guid.NewGuid(), "   "), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("EMPTY_BODY");
    }

    [Fact]
    public async Task Handle_creates_the_comment_and_returns_it()
    {
        var (task, author) = SeedTaskAndAuthor();

        var result = await CreateHandler().Handle(
            new AddCommentCommand(task.Id, author.Id, "Looks good to me"), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Body.Should().Be("Looks good to me");
        result.Data.AuthorName.Should().Be("Priya PM");
        _comments.Verify(c => c.AddAsync(It.IsAny<TaskComment>(), default), Times.Once);
    }

    [Fact]
    public async Task Handle_notifies_a_mentioned_project_member_in_app_and_by_email()
    {
        var (task, author) = SeedTaskAndAuthor();
        var mentioned = Engineer.Create("James Murphy", "james@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(mentioned.Id, default)).ReturnsAsync(mentioned);
        _access
            .Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default))
            .ReturnsAsync(new[] { mentioned.Id });
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(mentioned.Id)), default))
            .ReturnsAsync(new[] { mentioned });

        var result = await CreateHandler().Handle(
            new AddCommentCommand(task.Id, author.Id, "Hey @James Murphy can you take a look?"), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == mentioned.Id && x.Kind == NotificationKind.Mentioned),
            default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(mentioned.Email, It.Is<string>(s => s.Contains(task.Title)), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Handle_notifies_the_tasks_creator_even_with_no_other_project_access()
    {
        var creator = Engineer.Create("Priya Filer", "priya.f@test.io", "hash", Roles.ProductManager, 20, 14);
        var task = PulseTask.Create("Rebuild pipeline", 3, Guid.NewGuid(), createdById: creator.Id);
        var author = Engineer.Create("Some Engineer", "eng@test.io", "hash", Roles.Engineer, 20, 14);
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(author.Id, default)).ReturnsAsync(author);
        _engineers.Setup(e => e.GetByIdAsync(creator.Id, default)).ReturnsAsync(creator);
        // Access-set mock deliberately returns nothing — the creator must still be notified.
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(Array.Empty<Guid>());
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(creator.Id)), default))
            .ReturnsAsync(new[] { creator });

        var result = await CreateHandler().Handle(
            new AddCommentCommand(task.Id, author.Id, "@Priya Filer can you confirm the scope?"), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == creator.Id && x.Kind == NotificationKind.Mentioned),
            default), Times.Once);
    }

    [Fact]
    public async Task Handle_does_not_notify_a_mentioned_name_who_is_not_a_project_member()
    {
        var (task, author) = SeedTaskAndAuthor();
        // No accessible engineers seeded (default setup) — "@Someone Else" doesn't match any known candidate.

        var result = await CreateHandler().Handle(
            new AddCommentCommand(task.Id, author.Id, "cc @Someone Else"), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_does_not_notify_when_the_author_mentions_themselves()
    {
        var (task, author) = SeedTaskAndAuthor();
        _access
            .Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default))
            .ReturnsAsync(new[] { author.Id });
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(author.Id)), default))
            .ReturnsAsync(new[] { author });

        await CreateHandler().Handle(
            new AddCommentCommand(task.Id, author.Id, $"Note to self @{author.Name}"), default);

        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    // ── Executives: read-only everywhere else, but may comment ────────────────────────────────────────────────────────────────────────────

    private (PulseTask Task, Engineer Exec, Engineer Assignee, Engineer Creator) SeedExecutiveScenario()
    {
        // The access policy refuses an Executive (not a global role): the comment has to get through on the Executive rule alone.
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), Roles.Executive, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var creator = Engineer.Create("Cara Creator", "cara@test.io", "hash", Roles.ProjectManager, 20, 14);
        var assignee = Engineer.Create("Ade Assignee", "ade@test.io", "hash", Roles.Engineer, 20, 14);
        var exec = Engineer.Create("Eve Exec", "eve@test.io", "hash", Roles.Executive, 20, 14);
        var task = PulseTask.Create("Ship treasury", 3, Guid.NewGuid(), createdById: creator.Id,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(assignee.Id, creator.Id);

        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _projects.Setup(p => p.GetByIdAsync(task.ProjectId, default)).ReturnsAsync(Pulse.Domain.Projects.Project.Create("Treasury"));
        _engineers.Setup(e => e.GetByIdAsync(exec.Id, default)).ReturnsAsync(exec);
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(assignee.Id) || ids.Contains(creator.Id)), default))
            .ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => new[] { assignee, creator }.Where(e => ids.Contains(e.Id)).ToList());
        return (task, exec, assignee, creator);
    }

    [Fact]
    public async Task An_executive_can_comment_even_though_the_access_policy_refuses_them()
    {
        var (task, exec, _, _) = SeedExecutiveScenario();

        var result = await CreateHandler().Handle(new AddCommentCommand(task.Id, exec.Id, "Please prioritise this", Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        _comments.Verify(c => c.AddAsync(It.Is<TaskComment>(x => x.AuthorId == exec.Id), default), Times.Once);
    }

    [Fact]
    public async Task An_executives_comment_notifies_the_assignee_and_the_creator_in_app_and_by_email()
    {
        var (task, exec, assignee, creator) = SeedExecutiveScenario();

        await CreateHandler().Handle(new AddCommentCommand(task.Id, exec.Id, "Please prioritise this", Roles.Executive), default);

        foreach (var person in new[] { assignee, creator })
        {
            _notifications.Verify(n => n.AddAsync(
                It.Is<Notification>(x => x.UserId == person.Id && x.Kind == NotificationKind.ExecutiveCommented), default), Times.Once);
            _emailQueue.Verify(q => q.Enqueue(person.Email, It.Is<string>(s => s.Contains(task.Title)), It.IsAny<string>()), Times.Once);
        }
        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == exec.Id), default), Times.Never);
    }

    [Fact]
    public async Task Someone_the_executive_mentions_gets_the_mention_not_a_second_alert()
    {
        var (task, exec, assignee, creator) = SeedExecutiveScenario();
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(new[] { assignee.Id });

        await CreateHandler().Handle(new AddCommentCommand(task.Id, exec.Id, "@Ade Assignee please look", Roles.Executive), default);

        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == assignee.Id && x.Kind == NotificationKind.Mentioned), default), Times.Once);
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == assignee.Id && x.Kind == NotificationKind.ExecutiveCommented), default), Times.Never);
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == creator.Id && x.Kind == NotificationKind.ExecutiveCommented), default), Times.Once);
    }

    [Fact]
    public async Task An_ordinary_comment_does_not_send_the_executive_notification()
    {
        var (task, author) = SeedTaskAndAuthor();

        await CreateHandler().Handle(new AddCommentCommand(task.Id, author.Id, "fyi"), default);

        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.Kind == NotificationKind.ExecutiveCommented), default), Times.Never);
    }

    [Fact]
    public async Task An_executive_cannot_comment_on_someones_personal_task()
    {
        var (task, exec, _, _) = SeedExecutiveScenario();
        _projects.Setup(p => p.GetByIdAsync(task.ProjectId, default))
            .ReturnsAsync(Pulse.Domain.Projects.Project.CreatePersonal(Guid.NewGuid(), "Someone", "PERS1"));

        var result = await CreateHandler().Handle(new AddCommentCommand(task.Id, exec.Id, "peeking", Roles.Executive), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _comments.Verify(c => c.AddAsync(It.IsAny<TaskComment>(), default), Times.Never);
    }

    [Fact]
    public async Task An_executive_gets_not_found_for_a_task_that_does_not_exist()
    {
        var (_, exec, _, _) = SeedExecutiveScenario();

        var result = await CreateHandler().Handle(new AddCommentCommand(Guid.NewGuid(), exec.Id, "hello", Roles.Executive), default);

        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Other_read_only_viewers_still_cannot_comment(string role)
    {
        var (task, _, _, _) = SeedExecutiveScenario();
        _access.Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), role, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateHandler().Handle(new AddCommentCommand(task.Id, Guid.NewGuid(), "hello", role), default);

        result.ErrorCode.Should().Be("FORBIDDEN");
    }
}
