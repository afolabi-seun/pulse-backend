using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Estimation;

public class EstimateApprovalEscalationScannerTests
{
    private readonly Mock<IEstimationRepository> _estimation = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly OverworkThresholds _thresholds = new();

    public EstimateApprovalEscalationScannerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private EstimateApprovalEscalationScanner CreateScanner() => new(
        _estimation.Object, _tasks.Object, _engineers.Object, _teams.Object,
        _settings.Object, _thresholds, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (PulseTask Task, Engineer Assignee, Engineer TeamLead, Engineer Head, TaskEstimationSession Session) SeedPendingSession()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var teamLead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 20, 14);
        team.SetTeamLead(teamLead.Id);
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assignee.Id, Guid.NewGuid());

        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(teamLead.Id, default)).ReturnsAsync(teamLead);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, teamLead, head]);

        var session = TaskEstimationSession.Create(task.Id);
        session.Reveal();
        session.SubmitForApproval(5, Guid.NewGuid());
        _estimation.Setup(e => e.GetPendingApprovalsOlderThanAsync(It.IsAny<DateTime>(), default)).ReturnsAsync([session]);

        return (task, assignee, teamLead, head, session);
    }

    [Fact]
    public async Task Escalates_and_notifies_the_department_head_once()
    {
        var (_, _, _, head, session) = SeedPendingSession();

        await CreateScanner().RunAsync();

        session.EscalatedToHead.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == head.Id && x.Kind == NotificationKind.EstimateApprovalEscalated), default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(head.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Does_not_notify_the_team_lead_who_already_knows()
    {
        var (_, _, teamLead, _, _) = SeedPendingSession();

        await CreateScanner().RunAsync();

        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == teamLead.Id), default), Times.Never);
    }

    [Fact]
    public async Task Skips_a_session_already_escalated()
    {
        var (_, _, _, head, _) = SeedPendingSession();
        _estimation.Setup(e => e.GetPendingApprovalsOlderThanAsync(It.IsAny<DateTime>(), default)).ReturnsAsync([]); // repo's own filter already excludes it

        await CreateScanner().RunAsync();

        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == head.Id), default), Times.Never);
    }

    [Fact]
    public async Task Skips_a_session_already_at_the_department_head_stage()
    {
        var team = Team.Create("Platform"); // no team lead set — already DepartmentHead stage
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assignee.Id, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        var session = TaskEstimationSession.Create(task.Id);
        session.Reveal();
        session.SubmitForApproval(5, Guid.NewGuid());
        _estimation.Setup(e => e.GetPendingApprovalsOlderThanAsync(It.IsAny<DateTime>(), default)).ReturnsAsync([session]);

        await CreateScanner().RunAsync();

        session.EscalatedToHead.Should().BeFalse("there's no Team Lead tier to escalate past — it already resolves to the head");
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }
}
