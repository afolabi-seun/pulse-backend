using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Notifications;

public class CleanupOrphanedEscalationNotificationsHandlerTests
{
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    private CleanupOrphanedEscalationNotificationsHandler CreateHandler() =>
        new(_notifications.Object, _tasks.Object, _engineers.Object, _access.Object);

    private static PulseTask ActiveTask(Guid? assigneeId = null)
    {
        var task = PulseTask.Create("Overdue task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        if (assigneeId.HasValue) task.Assign(assigneeId.Value, Guid.NewGuid());
        return task;
    }

    private static string PayloadFor(Guid taskId) =>
        $$"""{"taskId":"{{taskId}}","taskTitle":"Overdue task","dueDate":"2026-08-01"}""";

    [Fact]
    public async Task Handle_never_deletes_the_assignees_own_copy()
    {
        var assigneeId = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        var notif = Notification.Create(assigneeId, NotificationKind.EscalationOverdue, PayloadFor(task.Id));
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(new[] { notif });
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Deleted.Should().Be(0);
        _notifications.Verify(n => n.DeleteAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_keeps_a_manager_notification_when_the_recipient_still_has_access()
    {
        var assigneeId = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        var manager = Engineer.Create("Priya PM", "priya@test.io", "hash", Roles.ProjectManager, 20, 14);
        var notif = Notification.Create(manager.Id, NotificationKind.EscalationOverdue, PayloadFor(task.Id));
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(new[] { notif });
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(manager.Id, default)).ReturnsAsync(manager);
        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, manager.Id, manager.Role, default)).ReturnsAsync(true);

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.Data!.Deleted.Should().Be(0);
        _notifications.Verify(n => n.DeleteAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_deletes_a_manager_notification_when_the_recipient_has_no_access()
    {
        var assigneeId = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        var outsider = Engineer.Create("Head Without Access", "headno@test.io", "hash", Roles.HeadOfDesign, 20, 14);
        var notif = Notification.Create(outsider.Id, NotificationKind.EscalationOverdue, PayloadFor(task.Id));
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(new[] { notif });
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(outsider.Id, default)).ReturnsAsync(outsider);
        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, outsider.Id, outsider.Role, default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.Data!.Checked.Should().Be(1);
        result.Data!.Deleted.Should().Be(1);
        _notifications.Verify(n => n.DeleteAsync(notif, default), Times.Once);
    }

    [Fact]
    public async Task Handle_deletes_a_notification_whose_task_no_longer_exists()
    {
        var taskId = Guid.NewGuid();
        var notif = Notification.Create(Guid.NewGuid(), NotificationKind.EscalationOverdue, PayloadFor(taskId));
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(new[] { notif });
        _tasks.Setup(t => t.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.Data!.Deleted.Should().Be(1);
        _notifications.Verify(n => n.DeleteAsync(notif, default), Times.Once);
    }

    [Fact]
    public async Task Handle_leaves_a_malformed_payload_alone()
    {
        var notif = Notification.Create(Guid.NewGuid(), NotificationKind.EscalationOverdue, "not json");
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(new[] { notif });

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.Data!.Checked.Should().Be(1);
        result.Data!.Deleted.Should().Be(0);
        _notifications.Verify(n => n.DeleteAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Handle_is_idempotent_when_run_again_with_nothing_left_to_delete()
    {
        _notifications.Setup(n => n.ListByKindAsync(NotificationKind.EscalationOverdue, default)).ReturnsAsync(Array.Empty<Notification>());

        var result = await CreateHandler().Handle(new CleanupOrphanedEscalationNotificationsCommand(), default);

        result.Data!.Checked.Should().Be(0);
        result.Data!.Deleted.Should().Be(0);
    }
}
