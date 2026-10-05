using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Notifications.Commands;

public record CleanupOrphanedEscalationNotificationsCommand : IRequest<ServiceResult<CleanupSummaryDto>>;

public record CleanupSummaryDto(int Checked, int Deleted);

/// <summary>
/// One-time cleanup for EscalationOverdue notifications created before the overdue-audience
/// scoping fix (see EscalationScanner.FireOverdueAsync) — back then, every PM and department head
/// org-wide got notified about every overdue task, regardless of whether they could even access
/// its project. Re-checks each existing notification's recipient against the same access rule the
/// scanner now applies going forward, and deletes the ones that no longer (or never did) pass it.
/// Safe to run more than once — a second run finds nothing left to delete.
/// </summary>
public class CleanupOrphanedEscalationNotificationsHandler
    : IRequestHandler<CleanupOrphanedEscalationNotificationsCommand, ServiceResult<CleanupSummaryDto>>
{
    private readonly INotificationRepository _notifications;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public CleanupOrphanedEscalationNotificationsHandler(
        INotificationRepository notifications, ITaskRepository tasks, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _notifications = notifications;
        _tasks = tasks;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<CleanupSummaryDto>> Handle(CleanupOrphanedEscalationNotificationsCommand request, CancellationToken ct)
    {
        var candidates = await _notifications.ListByKindAsync(NotificationKind.EscalationOverdue, ct);
        var deleted = 0;

        foreach (var notification in candidates)
        {
            var taskId = ExtractTaskId(notification.Payload);
            if (taskId is null) continue; // malformed payload — leave alone, not this cleanup's concern

            var task = await _tasks.GetByIdAsync(taskId.Value, ct);
            if (task is null)
            {
                // The task itself is gone — nothing left for this notification to point at.
                await _notifications.DeleteAsync(notification, ct);
                deleted++;
                continue;
            }

            // The assignee's own copy is always valid — only the broadcast "manager" copies were
            // ever at risk of over-reaching.
            if (task.AssigneeId == notification.UserId) continue;

            var recipient = await _engineers.GetByIdAsync(notification.UserId, ct);
            if (recipient is null)
            {
                await _notifications.DeleteAsync(notification, ct);
                deleted++;
                continue;
            }

            var allowed = await _access.CanAccessProjectAsync(task.ProjectId, notification.UserId, recipient.Role, ct);
            if (!allowed)
            {
                await _notifications.DeleteAsync(notification, ct);
                deleted++;
            }
        }

        await _notifications.SaveChangesAsync(ct);

        return ServiceResult<CleanupSummaryDto>.Ok(new CleanupSummaryDto(candidates.Count, deleted));
    }

    private static Guid? ExtractTaskId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(payload);
            return doc.TryGetProperty("taskId", out var prop) && prop.TryGetGuid(out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }
}
