using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class ProposeQaRejectionHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public ProposeQaRejectionHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private ProposeQaRejectionHandler CreateHandler() =>
        new(_tasks.Object, _audit.Object, _notifications.Object, _realtime.Object,
            _engineers.Object, _teams.Object, _emailQueue.Object, _settings.Object);

    internal static (PulseTask Parent, PulseTask Qa) InQaPair(Guid? qaAssigneeId = null)
    {
        var parent = PulseTask.Create("Original task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        parent.SetRequiresQa(true);
        parent.SendToQa(Guid.NewGuid());

        var qa = PulseTask.Create("[QA] Original task", 3, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        qa.SetParentTaskId(parent.Id);
        parent.SetQaTaskId(qa.Id);
        if (qaAssigneeId.HasValue)
            qa.Assign(qaAssigneeId.Value, Guid.NewGuid());

        return (parent, qa);
    }

    /// <summary>Sets up an engineer with a team in the given department, and stubs the repository
    /// lookups CanRejectAsync needs to resolve it.</summary>
    internal Engineer SeedEngineerInDepartment(string department)
    {
        var team = Team.Create($"Team {Guid.NewGuid():N}", department: department);
        var engineer = Engineer.Create($"Engineer {Guid.NewGuid():N}", $"{Guid.NewGuid():N}@test.io", "hash", Roles.Engineer, 20, 14);
        engineer.AssignToTeam(team.Id);

        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        return engineer;
    }

    [Fact]
    public async Task Returns_NOT_FOUND_when_qa_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(Guid.NewGuid(), "Not good enough", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_task_has_no_parent()
    {
        var notAQaTask = PulseTask.Create("Just a task", 2, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(notAQaTask.Id, default)).ReturnsAsync(notAQaTask);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(notAQaTask.Id, "Not good enough", Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_for_an_unrelated_engineer()
    {
        var (parent, qa) = InQaPair(qaAssigneeId: Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(qa.Id, "Not good enough", Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        parent.PendingRejectionReason.Should().BeNull("an unauthorized caller must not be able to mutate the task");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_for_a_department_head_whose_department_does_not_match_the_reviewer()
    {
        var reviewer = SeedEngineerInDepartment("Product");
        var (parent, qa) = InQaPair(qaAssigneeId: reviewer.Id);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var wrongDeptHead = SeedEngineerInDepartment("Engineering");

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(qa.Id, "Not good enough", wrongDeptHead.Id, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Succeeds_when_actor_is_qa_task_assignee_regardless_of_role()
    {
        var actorId = Guid.NewGuid();
        var (parent, qa) = InQaPair(qaAssigneeId: actorId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(qa.Id, "Not good enough", actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Records_the_pending_reason_without_touching_status_or_the_QA_sub_task()
    {
        var actorId = Guid.NewGuid();
        var (parent, qa) = InQaPair(qaAssigneeId: actorId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(qa.Id, "Missing edge case coverage", actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        // Nothing about the review moves yet — that only happens on confirm.
        parent.Status.Should().Be(DomainTaskStatus.InQa);
        parent.PendingRejectionReason.Should().Be("Missing edge case coverage");
        parent.QaTaskId.Should().NotBeNull();
        qa.Status.Should().NotBe(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task Notifies_the_assignee_with_the_proposer_named()
    {
        var actorId = Guid.NewGuid();
        var proposer = Engineer.Create("Riley Reviewer", "riley@test.io", "hash", Roles.Engineer, 20, 14);
        var (parent, qa) = InQaPair(qaAssigneeId: actorId);
        parent.Assign(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(actorId, default)).ReturnsAsync(proposer);
        _engineers.Setup(e => e.GetByIdAsync(parent.AssigneeId!.Value, default))
            .ReturnsAsync(Engineer.Create("Assignee", "assignee@test.io", "hash", Roles.Engineer, 20, 14));

        Notification? captured = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => captured = n);

        var result = await CreateHandler().Handle(
            new ProposeQaRejectionCommand(qa.Id, "Missing edge case coverage", actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.PendingRejectionActorName.Should().Be("Riley Reviewer");
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NotificationKind.QaRejectionProposed);
        captured!.Payload.Should().Contain("Riley Reviewer");
    }
}
