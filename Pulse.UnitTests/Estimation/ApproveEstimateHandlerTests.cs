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

public class ApproveEstimateHandlerTests
{
    private readonly Mock<IEstimationRepository> _estimation = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public ApproveEstimateHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private ApproveEstimateHandler CreateHandler() => new(
        _estimation.Object, _tasks.Object, _engineers.Object, _teams.Object,
        _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (Team team, Engineer assignee, Engineer head, PulseTask task, Guid submitterId) SetUpPendingEstimate(int points)
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
        _engineers.Setup(r => r.GetByIdAsync(submitterId, default)).ReturnsAsync(assignee); // submitter identity doesn't matter for these tests
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        return (team, assignee, head, task, submitterId);
    }

    [Fact]
    public async Task Fails_with_NOT_PENDING_when_nothing_is_awaiting_approval()
    {
        var taskId = Guid.NewGuid();
        _estimation.Setup(e => e.GetSessionAsync(taskId, default)).ReturnsAsync((TaskEstimationSession?)null);

        var result = await CreateHandler().Handle(new ApproveEstimateCommand(taskId, Guid.NewGuid(), Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_PENDING");
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_for_someone_who_is_not_the_resolved_department_head()
    {
        var (_, _, _, task, _) = SetUpPendingEstimate(8);
        var someoneElse = Guid.NewGuid();

        var result = await CreateHandler().Handle(new ApproveEstimateCommand(task.Id, someoneElse, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.Points.Should().Be(0);
    }

    [Fact]
    public async Task Department_head_approving_writes_points_and_closes_the_session()
    {
        var (_, _, head, task, submitterId) = SetUpPendingEstimate(8);

        var result = await CreateHandler().Handle(new ApproveEstimateCommand(task.Id, head.Id, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(8);
        _estimation.Verify(e => e.DeleteSessionAndVotesAsync(task.Id, default), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == submitterId), default), Times.Once);
    }

    [Fact]
    public async Task Falls_back_to_TeamLeadOrAbove_when_no_department_head_resolves()
    {
        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid()); // no team at all

        var session = TaskEstimationSession.Create(task.Id);
        session.Reveal();
        session.SubmitForApproval(5, Guid.NewGuid());
        _estimation.Setup(e => e.GetSessionAsync(task.Id, default)).ReturnsAsync(session);
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var teamLeadId = Guid.NewGuid();
        var result = await CreateHandler().Handle(new ApproveEstimateCommand(task.Id, teamLeadId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(5);
    }
}
