using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Estimation;

public class SubmitEstimateForApprovalHandlerTests
{
    private readonly Mock<IEstimationRepository> _estimation = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public SubmitEstimateForApprovalHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private SubmitEstimateForApprovalHandler CreateHandler() => new(
        _estimation.Object, _tasks.Object, _engineers.Object, _teams.Object,
        _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object);

    private void SetUpRevealedSession(Guid taskId)
    {
        var session = TaskEstimationSession.Create(taskId);
        session.Reveal();
        _estimation.Setup(e => e.GetSessionAsync(taskId, default)).ReturnsAsync(session);
    }

    [Fact]
    public async Task Rejects_submitting_an_estimate_when_task_has_no_due_date()
    {
        var task = PulseTask.Create("Task with no due date", 0, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.Points.Should().Be(0);
    }

    [Fact]
    public async Task Submitting_does_not_write_points_directly_it_only_marks_the_session_pending()
    {
        var actorId = Guid.NewGuid();
        var task = PulseTask.Create("Task with a due date", 0, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 8, actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(0, "points are only written once a department head approves");
        _estimation.Verify(e => e.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Notifies_the_resolved_department_head_by_email_and_in_app()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assignee.Id, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == head.Id), default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(head.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task No_notification_sent_when_no_department_head_can_be_resolved()
    {
        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid()); // assignee has no team at all
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task The_tasks_own_assignee_can_submit_without_being_team_lead_or_above()
    {
        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        var assigneeId = Guid.NewGuid();
        task.Assign(assigneeId, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, assigneeId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_engineer_who_is_neither_the_assignee_nor_team_lead_or_above_is_forbidden()
    {
        var task = PulseTask.Create("Task", 0, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        SetUpRevealedSession(task.Id);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _estimation.Verify(e => e.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Notifies_the_assignees_team_lead_first_when_one_is_set()
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
        SetUpRevealedSession(task.Id);

        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(teamLead.Id, default)).ReturnsAsync(teamLead);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        var result = await CreateHandler().Handle(new SubmitEstimateForApprovalCommand(task.Id, 5, assignee.Id, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == teamLead.Id), default), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == head.Id), default), Times.Never);
    }
}
