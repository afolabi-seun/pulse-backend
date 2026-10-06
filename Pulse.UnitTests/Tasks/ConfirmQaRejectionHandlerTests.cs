using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class ConfirmQaRejectionHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public ConfirmQaRejectionHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private ConfirmQaRejectionHandler CreateHandler() =>
        new(_tasks.Object, _audit.Object, _engineers.Object, _teams.Object, _projects.Object, _settings.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private static (PulseTask Parent, PulseTask Qa) PendingRejectionPair(Guid reviewerId, string reason = "Not good enough", TaskStage? targetStage = null)
    {
        var (parent, qa) = ProposeQaRejectionHandlerTests.InQaPair(qaAssigneeId: reviewerId);
        parent.ProposeQaRejection(reason, reviewerId, targetStage);
        return (parent, qa);
    }

    /// <summary>A RequiresFrontendHandoff task, at the Frontend stage, InQa — the shape needed for
    /// backend-flagged auto-routing on confirm. Mirrors ProposeQaRejectionHandlerTests.InQaPair,
    /// but built up through the real backend→frontend handoff so BackendAssigneeId is populated.
    /// Takes the backend engineer's id as a parameter so callers needing a real Engineer mock for
    /// it (rather than a bare Guid) can pass that engineer's own id.</summary>
    private static (PulseTask Parent, PulseTask Qa) InQaPairAtFrontendStage(Guid reviewerId, Guid backendEngineerId)
    {
        var frontendEngineerId = Guid.NewGuid();
        var parent = PulseTask.Create("Original task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        parent.Assign(backendEngineerId, backendEngineerId);
        parent.SetRequiresFrontendHandoff(true);
        parent.HandOffToFrontend(frontendEngineerId, backendEngineerId);
        parent.SetRequiresQa(true);
        parent.SendToQa(frontendEngineerId);

        var qa = PulseTask.Create("[QA] Original task", 3, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        qa.SetParentTaskId(parent.Id);
        parent.SetQaTaskId(qa.Id);
        qa.Assign(reviewerId, Guid.NewGuid());

        return (parent, qa);
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_nothing_is_pending()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = ProposeQaRejectionHandlerTests.InQaPair(qaAssigneeId: reviewerId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_for_an_unrelated_engineer()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = PendingRejectionPair(reviewerId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(
            new ConfirmQaRejectionCommand(qa.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        parent.Status.Should().Be(DomainTaskStatus.InQa);
    }

    [Fact]
    public async Task Reactivates_the_parent_with_the_pending_reason_and_closes_the_QA_subtask()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = PendingRejectionPair(reviewerId, "Missing edge case coverage");
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        parent.Status.Should().Be(DomainTaskStatus.Active);
        parent.ReactivationReason.Should().Be("Missing edge case coverage");
        parent.QaTaskId.Should().BeNull();
        parent.PendingRejectionReason.Should().BeNull();
        qa.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task Notifies_the_assignee_with_the_confirmer_named()
    {
        var reviewerId = Guid.NewGuid();
        var confirmer = Engineer.Create("Riley Reviewer", "riley@test.io", "hash", Roles.Engineer, 20, 14);
        var (parent, qa) = PendingRejectionPair(reviewerId, "Missing edge case coverage");
        parent.Assign(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(reviewerId, default)).ReturnsAsync(confirmer);
        _engineers.Setup(e => e.GetByIdAsync(parent.AssigneeId!.Value, default))
            .ReturnsAsync(Engineer.Create("Assignee", "assignee@test.io", "hash", Roles.Engineer, 20, 14));

        Notification? captured = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => captured = n);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.ReactivatedByName.Should().Be("Riley Reviewer");
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NotificationKind.QaRejected);
        captured!.Payload.Should().Contain("Riley Reviewer");
    }

    // ── backend-flagged auto-routing ─────────────────────────────────────────

    [Fact]
    public async Task Auto_routes_to_the_backend_engineer_when_flagged_Backend_at_the_Frontend_stage()
    {
        var reviewerId = Guid.NewGuid();
        var backendEngineer = Engineer.Create("Backend Bob", "bob@test.io", "hash", Roles.Engineer, 20, 14);
        backendEngineer.SetDiscipline(Discipline.Backend);
        var (parent, qa) = InQaPairAtFrontendStage(reviewerId, backendEngineer.Id);
        parent.ProposeQaRejection("Actually a backend bug", reviewerId, TaskStage.Backend);

        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(backendEngineer.Id, default)).ReturnsAsync(backendEngineer);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        parent.Status.Should().Be(DomainTaskStatus.Active);
        parent.CurrentStage.Should().Be(TaskStage.Backend);
        parent.AssigneeId.Should().Be(backendEngineer.Id);
        _projects.Verify(p => p.AddMemberAsync(parent.ProjectId, backendEngineer.Id, default), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == backendEngineer.Id && x.Kind == NotificationKind.TaskAssigned), default), Times.Once);
    }

    [Fact]
    public async Task Does_not_auto_route_when_QA_flags_Frontend_instead()
    {
        // The reverse direction has no stored frontend assignee to route to — stays manual.
        var reviewerId = Guid.NewGuid();
        var backendEngineerId = Guid.NewGuid();
        var (parent, qa) = InQaPairAtFrontendStage(reviewerId, backendEngineerId);
        parent.ProposeQaRejection("Actually a frontend bug", reviewerId, TaskStage.Frontend);

        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        parent.CurrentStage.Should().Be(TaskStage.Frontend);
        parent.AssigneeId.Should().NotBe(backendEngineerId);
        _projects.Verify(p => p.AddMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task Does_not_auto_route_when_the_stored_backend_engineer_is_inactive()
    {
        var reviewerId = Guid.NewGuid();
        var backendEngineer = Engineer.Create("Backend Bob", "bob@test.io", "hash", Roles.Engineer, 20, 14);
        backendEngineer.SetDiscipline(Discipline.Backend);
        backendEngineer.Deactivate();
        var (parent, qa) = InQaPairAtFrontendStage(reviewerId, backendEngineer.Id);
        parent.ProposeQaRejection("Actually a backend bug", reviewerId, TaskStage.Backend);

        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(backendEngineer.Id, default)).ReturnsAsync(backendEngineer);

        var result = await CreateHandler().Handle(new ConfirmQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        // Confirming the rejection is still a valid, separate outcome — an unroutable auto-hand-off
        // just means it's left for a human to sort out manually, not a reason to fail the whole confirm.
        result.IsSuccess.Should().BeTrue();
        parent.Status.Should().Be(DomainTaskStatus.Active);
        parent.CurrentStage.Should().Be(TaskStage.Frontend);
        _projects.Verify(p => p.AddMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), default), Times.Never);
    }
}
