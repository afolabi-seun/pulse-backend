using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class RespondToQaRejectionHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<INotificationRepository> _notifications = new();

    private RespondToQaRejectionHandler CreateHandler() =>
        new(_tasks.Object, _audit.Object, _access.Object, _engineers.Object, TestNotifications.Dispatcher(_notifications, _realtime));

    private static PulseTask PendingRejectionTask(Guid assigneeId, Guid reviewerId)
    {
        var task = PulseTask.Create("Original task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        task.SetRequiresQa(true);
        task.Assign(assigneeId, Guid.NewGuid());
        task.SendToQa(Guid.NewGuid());
        task.ProposeQaRejection("Not good enough", reviewerId);
        return task;
    }

    [Fact]
    public async Task Returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new RespondToQaRejectionCommand(Guid.NewGuid(), "It's an environment issue", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_actor_is_not_the_assignee_and_has_no_project_access()
    {
        var task = PendingRejectionTask(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new RespondToQaRejectionCommand(task.Id, "It's an environment issue", Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.PendingRejectionResponse.Should().BeNull();
    }

    [Fact]
    public async Task The_assignee_can_respond_without_any_project_access_check()
    {
        var assigneeId = Guid.NewGuid();
        var task = PendingRejectionTask(assigneeId, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new RespondToQaRejectionCommand(task.Id, "It's an environment issue", assigneeId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.PendingRejectionResponse.Should().Be("It's an environment issue");
        task.PendingRejectionRespondedByEngineerId.Should().Be(assigneeId);
        _access.Verify(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Notifies_the_QA_reviewer_who_proposed_the_rejection()
    {
        var assigneeId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var task = PendingRejectionTask(assigneeId, reviewerId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(assigneeId, default))
            .ReturnsAsync(Engineer.Create("Responder", "responder@test.io", "hash", Roles.Engineer, 20, 14));

        Notification? captured = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => captured = n);

        var result = await CreateHandler().Handle(
            new RespondToQaRejectionCommand(task.Id, "It's an environment issue", assigneeId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(reviewerId);
        captured!.Kind.Should().Be(NotificationKind.QaRejectionResponded);
    }
}
