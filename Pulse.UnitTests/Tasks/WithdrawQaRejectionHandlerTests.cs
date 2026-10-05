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

public class WithdrawQaRejectionHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();

    private WithdrawQaRejectionHandler CreateHandler() =>
        new(_tasks.Object, _audit.Object, _notifications.Object, _realtime.Object, _engineers.Object, _teams.Object);

    private static (PulseTask Parent, PulseTask Qa) PendingRejectionPair(Guid reviewerId, string reason = "Not good enough")
    {
        var (parent, qa) = ProposeQaRejectionHandlerTests.InQaPair(qaAssigneeId: reviewerId);
        parent.ProposeQaRejection(reason, reviewerId);
        return (parent, qa);
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_nothing_is_pending()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = ProposeQaRejectionHandlerTests.InQaPair(qaAssigneeId: reviewerId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(new WithdrawQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

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
            new WithdrawQaRejectionCommand(qa.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        parent.PendingRejectionReason.Should().NotBeNull();
    }

    [Fact]
    public async Task Clears_the_pending_rejection_without_touching_status_or_the_QA_subtask()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = PendingRejectionPair(reviewerId);
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var result = await CreateHandler().Handle(new WithdrawQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        parent.Status.Should().Be(DomainTaskStatus.InQa, "no iteration should be counted for a withdrawn rejection");
        parent.PendingRejectionReason.Should().BeNull();
        parent.QaTaskId.Should().NotBeNull();
        qa.Status.Should().NotBe(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task Notifies_the_assignee_that_the_rejection_was_withdrawn()
    {
        var reviewerId = Guid.NewGuid();
        var (parent, qa) = PendingRejectionPair(reviewerId);
        parent.Assign(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(qa.Id, default)).ReturnsAsync(qa);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        Notification? captured = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => captured = n);

        var result = await CreateHandler().Handle(new WithdrawQaRejectionCommand(qa.Id, reviewerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NotificationKind.QaRejectionWithdrawn);
    }
}
