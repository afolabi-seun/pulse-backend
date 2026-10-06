using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Estimation;

public class RejectEstimateHandlerTests
{
    private readonly Mock<IEstimationRepository> _estimation = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RejectEstimateHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private RejectEstimateHandler CreateHandler() => new(
        _estimation.Object, _tasks.Object, _engineers.Object, _teams.Object,
        _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (Engineer head, PulseTask task, TaskEstimationSession session, Guid submitterId) SetUpPendingEstimate(int points, int voteCount = 2)
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);
        var submitterId = Guid.NewGuid();

        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assignee.Id, Guid.NewGuid());

        var session = TaskEstimationSession.Create(task.Id);
        session.Reveal();
        session.SubmitForApproval(points, submitterId);

        _estimation.Setup(e => e.GetSessionAsync(task.Id, default)).ReturnsAsync(session);
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(submitterId, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        return (head, task, session, submitterId);
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_for_someone_who_is_not_the_resolved_department_head()
    {
        var (_, task, _, _) = SetUpPendingEstimate(8);

        var result = await CreateHandler().Handle(new RejectEstimateCommand(task.Id, "too high", Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Rejecting_clears_the_pending_request_but_leaves_the_session_and_votes_intact()
    {
        var (head, task, session, submitterId) = SetUpPendingEstimate(8);

        var result = await CreateHandler().Handle(new RejectEstimateCommand(task.Id, "let's re-discuss", head.Id, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        session.PendingApprovalPoints.Should().BeNull();
        session.IsRevealed.Should().BeTrue("only the pending request is cleared, not the whole session");
        task.Points.Should().Be(0, "a rejected estimate never touches the task");
        _estimation.Verify(e => e.DeleteSessionAndVotesAsync(It.IsAny<Guid>(), default), Times.Never);
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == submitterId), default), Times.Once);
    }
}
