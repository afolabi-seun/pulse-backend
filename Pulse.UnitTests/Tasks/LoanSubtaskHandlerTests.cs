using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class LoanSubtaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ISubtaskRepository> _subtasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public LoanSubtaskHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private LoanSubtaskHandler CreateHandler() =>
        new(_tasks.Object, _subtasks.Object, _engineers.Object, _teams.Object, _audit.Object, _projects.Object,
            _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object);

    private (PulseTask Task, Subtask Subtask, Engineer CurrentAssignee, Team CurrentTeam) SetUpTaskWithSubtask(Guid actorId)
    {
        var currentTeam = Team.Create("Platform", actorId, "Engineering");
        var currentAssignee = Engineer.Create("Current owner", "owner@test.io", "hash", Roles.Engineer, 20, 14);
        currentAssignee.AssignToTeam(currentTeam.Id);

        var task = PulseTask.Create("Ship the thing", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.Assign(currentAssignee.Id, actorId);

        var subtask = Subtask.Create(task.Id, "A checklist item", actorId);

        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);
        _engineers.Setup(e => e.GetByIdAsync(currentAssignee.Id, default)).ReturnsAsync(currentAssignee);

        return (task, subtask, currentAssignee, currentTeam);
    }

    [Fact]
    public async Task Rejects_loaning_to_the_same_department()
    {
        var actorId = Guid.NewGuid();
        var (task, subtask, _, currentTeam) = SetUpTaskWithSubtask(actorId);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { currentTeam });

        var sameDeptTarget = Engineer.Create("Same dept", "same@test.io", "hash", Roles.Engineer, 20, 14);
        sameDeptTarget.AssignToTeam(currentTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(sameDeptTarget.Id, default)).ReturnsAsync(sameDeptTarget);

        var result = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, sameDeptTarget.Id, null, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        subtask.AssigneeId.Should().BeNull("the rejected loan must not have applied");
    }

    [Fact]
    public async Task Rejects_when_team_lead_does_not_own_the_tasks_team()
    {
        var actorId = Guid.NewGuid();
        var (task, subtask, _, currentTeam) = SetUpTaskWithSubtask(actorId: Guid.NewGuid());
        var otherTeam = Team.Create("Some other team", actorId, "Product");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { currentTeam, otherTeam });

        var target = Engineer.Create("Cross-dept target", "target@test.io", "hash", Roles.Engineer, 20, 14);
        target.AssignToTeam(otherTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, target.Id, null, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        subtask.AssigneeId.Should().BeNull("the rejected loan must not have applied");
    }

    [Fact]
    public async Task Rejects_loaning_on_an_unassigned_task()
    {
        var actorId = Guid.NewGuid();
        var task = PulseTask.Create("Unassigned task", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        var subtask = Subtask.Create(task.Id, "A checklist item", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _subtasks.Setup(r => r.GetByIdAsync(subtask.Id, default)).ReturnsAsync(subtask);

        var result = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, Guid.NewGuid(), null, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Head_can_loan_a_subtask_to_a_different_department()
    {
        var actorId = Guid.NewGuid();
        var (task, subtask, _, currentTeam) = SetUpTaskWithSubtask(actorId);
        var otherTeam = Team.Create("Design team", Guid.NewGuid(), "Product");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { currentTeam, otherTeam });

        var target = Engineer.Create("Cross-dept target", "target@test.io", "hash", Roles.Engineer, 20, 14);
        target.AssignToTeam(otherTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, target.Id, "Need extra hands", actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().Be(target.Id);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, target.Id, default), Times.Once);
        _audit.Verify(a => a.LogAsync("SUBTASK_LOANED", actorId, null, It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task Re_loaning_an_already_loaned_subtask_checks_department_against_the_current_holder_not_the_task_assignee()
    {
        // The task's real assignee stays in "Engineering" for its whole lifetime. Once the subtask
        // has been loaned out to "Product", a second loan must be judged against Product (the
        // current holder), not Engineering (the task's original assignee) — otherwise a target back
        // in Engineering would wrongly look like a fresh cross-department move.
        var actorId = Guid.NewGuid();
        var (task, subtask, _, engineeringTeam) = SetUpTaskWithSubtask(actorId);
        var productTeam = Team.Create("Design", Guid.NewGuid(), "Product");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { engineeringTeam, productTeam });

        var firstBorrower = Engineer.Create("First borrower", "first@test.io", "hash", Roles.Engineer, 20, 14);
        firstBorrower.AssignToTeam(productTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(firstBorrower.Id, default)).ReturnsAsync(firstBorrower);

        var firstLoan = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, firstBorrower.Id, null, actorId, null, Roles.HeadOfRnD), default);
        firstLoan.IsSuccess.Should().BeTrue();

        // A second target back in Engineering — same department as the task's ORIGINAL assignee,
        // but a different department from the current holder (Product), so this must be allowed.
        var secondBorrower = Engineer.Create("Second borrower", "second@test.io", "hash", Roles.Engineer, 20, 14);
        secondBorrower.AssignToTeam(engineeringTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(secondBorrower.Id, default)).ReturnsAsync(secondBorrower);

        var secondLoan = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, secondBorrower.Id, null, actorId, null, Roles.HeadOfRnD), default);

        secondLoan.IsSuccess.Should().BeTrue();
        subtask.AssigneeId.Should().Be(secondBorrower.Id);
        subtask.LoanedFromEngineerId.Should().Be(firstBorrower.Id);
    }

    [Fact]
    public async Task Rejects_re_loaning_to_the_same_department_as_the_current_holder()
    {
        var actorId = Guid.NewGuid();
        var (task, subtask, _, engineeringTeam) = SetUpTaskWithSubtask(actorId);
        var productTeam = Team.Create("Design", Guid.NewGuid(), "Product");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { engineeringTeam, productTeam });

        var firstBorrower = Engineer.Create("First borrower", "first@test.io", "hash", Roles.Engineer, 20, 14);
        firstBorrower.AssignToTeam(productTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(firstBorrower.Id, default)).ReturnsAsync(firstBorrower);
        await CreateHandler().Handle(new LoanSubtaskCommand(task.Id, subtask.Id, firstBorrower.Id, null, actorId, null, Roles.HeadOfRnD), default);

        // Same department (Product) as the current holder — must be rejected even though it
        // differs from the task's original (Engineering) assignee.
        var sameDeptBorrower = Engineer.Create("Same dept borrower", "samedept@test.io", "hash", Roles.Engineer, 20, 14);
        sameDeptBorrower.AssignToTeam(productTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(sameDeptBorrower.Id, default)).ReturnsAsync(sameDeptBorrower);

        var result = await CreateHandler().Handle(
            new LoanSubtaskCommand(task.Id, subtask.Id, sameDeptBorrower.Id, null, actorId, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        subtask.AssigneeId.Should().Be(firstBorrower.Id, "the rejected re-loan must not have applied");
    }
}
