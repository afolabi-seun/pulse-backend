using Pulse.Application.Common.Interfaces;
using Pulse.Application.Feedback.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FeedbackEntity = Pulse.Domain.Feedback.Feedback;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Feedback;

public class ReplyToFeedbackHandlerTests
{
    private readonly Mock<IFeedbackRepository> _feedback = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    public ReplyToFeedbackHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private ReplyToFeedbackHandler CreateHandler() => new(
        _feedback.Object, _engineers.Object, _teams.Object,
        TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object, _audit.Object);

    [Fact]
    public async Task Fails_with_NOT_FOUND_when_the_feedback_entry_does_not_exist()
    {
        var id = Guid.NewGuid();
        _feedback.Setup(r => r.GetByIdAsync(id, default)).ReturnsAsync((FeedbackEntity?)null);

        var result = await CreateHandler().Handle(new ReplyToFeedbackCommand(id, "Thanks for raising this", Guid.NewGuid(), Roles.HeadOfRnD, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_when_the_head_is_in_a_different_department()
    {
        var deptA = Team.Create("Platform"); deptA.SetDepartment("Engineering");
        var deptB = Team.Create("Design"); deptB.SetDepartment("Design");
        var submitter = Engineer.Create("Sub", "sub@x.io", "hash", Roles.Engineer, 20, 14);
        submitter.AssignToTeam(deptA.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfDesign, 20, 14);
        head.AssignToTeam(deptB.Id);

        var entry = FeedbackEntity.Submit(submitter.Id, "Some concern", DateOnly.FromDateTime(DateTime.UtcNow));
        _feedback.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(submitter.Id, default)).ReturnsAsync(submitter);
        _teams.Setup(r => r.GetByIdAsync(deptA.Id, default)).ReturnsAsync(deptA);
        _teams.Setup(r => r.GetByIdAsync(deptB.Id, default)).ReturnsAsync(deptB);

        var result = await CreateHandler().Handle(new ReplyToFeedbackCommand(entry.Id, "Reply", head.Id, Roles.HeadOfDesign, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        entry.ReplyText.Should().BeNull();
    }

    [Fact]
    public async Task Head_of_the_same_department_can_reply_and_the_submitter_is_notified()
    {
        var team = Team.Create("Platform"); team.SetDepartment("Engineering");
        var submitter = Engineer.Create("Sub", "sub@x.io", "hash", Roles.Engineer, 20, 14);
        submitter.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        var entry = FeedbackEntity.Submit(submitter.Id, "Some concern", DateOnly.FromDateTime(DateTime.UtcNow));
        _feedback.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(submitter.Id, default)).ReturnsAsync(submitter);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var result = await CreateHandler().Handle(new ReplyToFeedbackCommand(entry.Id, "Thanks — let's talk", head.Id, Roles.HeadOfRnD, null), default);

        result.IsSuccess.Should().BeTrue();
        entry.ReplyText.Should().Be("Thanks — let's talk");
        entry.RepliedBy.Should().Be(head.Id);
        _feedback.Verify(r => r.SaveChangesAsync(default), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == submitter.Id), default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(submitter.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task HeadOfPmo_can_reply_to_any_departments_feedback()
    {
        var team = Team.Create("Design"); team.SetDepartment("Design");
        var submitter = Engineer.Create("Sub", "sub@x.io", "hash", Roles.Designer, 20, 14);
        submitter.AssignToTeam(team.Id);
        var pmoHead = Engineer.Create("Pmo", "pmo@x.io", "hash", Roles.HeadOfPmo, 20, 14); // no team of their own

        var entry = FeedbackEntity.Submit(submitter.Id, "Some concern", DateOnly.FromDateTime(DateTime.UtcNow));
        _feedback.Setup(r => r.GetByIdAsync(entry.Id, default)).ReturnsAsync(entry);
        _engineers.Setup(r => r.GetByIdAsync(pmoHead.Id, default)).ReturnsAsync(pmoHead);
        _engineers.Setup(r => r.GetByIdAsync(submitter.Id, default)).ReturnsAsync(submitter);

        var result = await CreateHandler().Handle(new ReplyToFeedbackCommand(entry.Id, "Noted, thank you", pmoHead.Id, Roles.HeadOfPmo, null), default);

        result.IsSuccess.Should().BeTrue();
        entry.ReplyText.Should().Be("Noted, thank you");
    }
}
