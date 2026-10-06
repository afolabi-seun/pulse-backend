using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record FlagBlockerCommand(
    Guid TaskId,
    string Reason,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class FlagBlockerHandler : IRequestHandler<FlagBlockerCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEpicRepository _epics;
    private readonly IProjectAccessPolicy _access;
    private readonly IEngineerRepository _engineers;
    private readonly IAppSettings _settings;
    private readonly INotificationDispatcher _notify;

    public FlagBlockerHandler(
        ITaskRepository tasks,
        IAuditLogRepository audit,
        IRealtimeNotifier realtime,
        IEpicRepository epics,
        IProjectAccessPolicy access,
        IEngineerRepository engineers,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _tasks = tasks;
        _audit = audit;
        _realtime = realtime;
        _epics = epics;
        _access = access;
        _engineers = engineers;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(FlagBlockerCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        // Same access model every other task action uses (see SendToQaCommand): the assignee
        // themselves, or anyone with general access to the task's project — which covers PMO,
        // department heads, and teammates, not just the one engineer it's assigned to.
        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        try
        {
            task.FlagBlocker(cmd.Reason, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        await EpicStatusRollUp.ApplyAsync(task.EpicId, _tasks, _epics, ct);

        await _audit.LogAsync("BLOCKER_FLAGGED", cmd.ActorId, cmd.IpAddress,
            $"Blocker flagged on task {task.Id}: {cmd.Reason}", ct);

        var dto = TaskDto.From(task);
        await _realtime.SendTaskUpdatedAsync(cmd.ActorId, dto, ct);

        // Only notify when someone else flagged it — an engineer flagging their own blocker
        // already knows; if it stays blocked, the escalation scanner picks up the team lead/PMO
        // notification path from there, so this doesn't need to duplicate that.
        if (task.AssigneeId is Guid assigneeId && assigneeId != cmd.ActorId)
        {
            var flagger = await _engineers.GetByIdAsync(cmd.ActorId, ct);
            var payload = JsonSerializer.Serialize(new
            {
                taskId      = task.Id,
                taskTitle   = task.Title,
                reason      = cmd.Reason,
                flaggedByName = flagger?.Name,
            });

            await _notify.NotifyAsync(assigneeId, NotificationKind.BlockerFlagged, payload, ct: ct);

            var assignee = await _engineers.GetByIdAsync(assigneeId, ct);
            if (assignee is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var flaggedBy = flagger is not null ? $" by <strong>{flagger.Name}</strong>" : string.Empty;
                var body = $"""
                    <p>Hi {assignee.Name},</p>
                    <p>A blocker was flagged{flaggedBy} on your task: <strong>{task.Title}</strong>.</p>
                    <p style="background:#fef2f2;border-left:4px solid #ef4444;padding:12px 16px;border-radius:4px;color:#991b1b;">
                      <strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(cmd.Reason)}
                    </p>
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because you are the assignee of this task.")}
                    """;
                await _notify.EmailAsync(assignee.Id, NotificationKind.BlockerFlagged, new NotificationEmail(assignee.Email, $"Blocker flagged: {task.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        await NotifyMentionsAsync(task, cmd.Reason, cmd.ActorId, ct);

        return ServiceResult<TaskDto>.Ok(dto);
    }

    /// <summary>Notifies "@Full Name" mentions found in the blocker reason — scoped to everyone
    /// who can access the task's project, same as comment mentions (see MentionParser /
    /// ProjectAccessPolicy.GetAccessibleEngineerIdsAsync), plus the task's own creator (see
    /// AddCommentCommand's identical addition for why). Separate from the assignee-notification
    /// above: the assignee (if mentioned) isn't double-notified since MentionParser only needs to
    /// exclude the actor, and a mention-specific notification with its own copy still adds signal
    /// beyond the generic "a blocker was flagged" one.</summary>
    private async Task NotifyMentionsAsync(Domain.Tasks.PulseTask task, string reason, Guid actorId, CancellationToken ct)
    {
        var accessibleIds = (await _access.GetAccessibleEngineerIdsAsync(task.ProjectId, ct)).ToHashSet();
        if (task.CreatedById is Guid creatorId)
            accessibleIds.Add(creatorId);
        if (accessibleIds.Count == 0) return;
        var candidates = await _engineers.GetByIdsAsync(accessibleIds.ToList(), ct);

        var mentioned = MentionParser.ExtractMentions(reason, candidates, actorId);
        if (mentioned.Count == 0) return;

        var actor = await _engineers.GetByIdAsync(actorId, ct);
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";

        foreach (var engineer in mentioned)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId          = task.Id,
                taskTitle       = task.Title,
                reason,
                mentionedByName = actor?.Name,
            });

            await _notify.NotifyAsync(engineer.Id, NotificationKind.Mentioned, payload, ct: ct);

            var mentionedBy = actor is not null ? $" by <strong>{actor.Name}</strong>" : string.Empty;
            var emailBody = $"""
                <p>Hi {engineer.Name},</p>
                <p>You were mentioned{mentionedBy} in a blocker on <strong>{task.Title}</strong>:</p>
                <p style="background:#f8fafc;border-left:4px solid #6366f1;padding:12px 16px;border-radius:4px;color:#334155;">
                  {System.Net.WebUtility.HtmlEncode(reason)}
                </p>
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because you were mentioned in a blocker.")}
                """;
            await _notify.EmailAsync(engineer.Id, NotificationKind.Mentioned, new NotificationEmail(engineer.Email, $"You were mentioned: {task.Title}", EmailTemplate.Layout(emailBody)), ct);
        }
    }
}
