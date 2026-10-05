using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class RecallSubtaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RecallSubtaskHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private RecallSubtaskHandler CreateHandler() =>
        new(_tasks.Object, _subtasks.Object, _engineers.Object, _teams.Object, _audit.Object,
            _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object);

    private (PulseTask Task, Subtask Subtask, Team Team) SetUpLoanedSubtask(Guid actorId, Guid borrowerId)
    {
        var team = Team.Create("Platform", actorId, "Engineering");
        var owner = Engineer.Create("Owner", "owner@test.io", "hash", Roles.Engineer, 20, 14);
        owner.AssignToTeam(team.Id);

        var task = PulseTask.Create("Ship the thing", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(owner.Id, actorId);

        var subtask = Subtask.Create(task.Id, "A checklist item", actorId);
        subtask.Loan(borrowerId);

        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { team });

        return (task, subtask, team);
    }

    [Fact]
    public async Task Rejects_recalling_a_subtask_that_is_not_on_loan()
    {
        var actorId = Guid.NewGuid();
        var task = PulseTask.Create("Task", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        var subtask = Subtask.Create(task.Id, "Never loaned", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Rejects_when_team_lead_does_not_own_the_tasks_team()
    {
        var borrowerId = Guid.NewGuid();
        var (task, subtask, _) = SetUpLoanedSubtask(actorId: Guid.NewGuid(), borrowerId);

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        subtask.AssigneeId.Should().Be(borrowerId, "the rejected recall must not have applied");
    }

    [Fact]
    public async Task Head_can_recall_a_loaned_subtask_back_to_unassigned()
    {
        var actorId = Guid.NewGuid();
        var borrowerId = Guid.NewGuid();
        var (task, subtask, _) = SetUpLoanedSubtask(actorId, borrowerId);

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().BeNull();
        _audit.Verify(a => a.LogAsync("SUBTASK_RECALLED", actorId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Team_lead_of_the_owning_team_can_recall()
    {
        var actorId = Guid.NewGuid();
        var borrowerId = Guid.NewGuid();
        var (task, subtask, _) = SetUpLoanedSubtask(actorId, borrowerId);

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task Recalling_a_re_loaned_subtask_restores_the_immediately_previous_borrower_not_null()
    {
        var actorId = Guid.NewGuid();
        var firstBorrowerId = Guid.NewGuid();
        var secondBorrowerId = Guid.NewGuid();
        var (task, subtask, _) = SetUpLoanedSubtask(actorId, firstBorrowerId);
        subtask.Loan(secondBorrowerId); // re-loaned without an intervening recall

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().Be(firstBorrowerId);
        subtask.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public async Task Allows_the_current_holder_to_recall_their_own_loaned_subtask_without_lead_or_head_privileges()
    {
        var actorId = Guid.NewGuid();
        var borrowerId = Guid.NewGuid();
        var (task, subtask, _) = SetUpLoanedSubtask(actorId, borrowerId);

        var result = await CreateHandler().Handle(
            new RecallSubtaskCommand(task.Id, subtask.Id, borrowerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().BeNull();
    }
}
