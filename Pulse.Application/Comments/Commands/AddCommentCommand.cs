using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Comments.Commands;

public record AddCommentCommand(Guid TaskId, Guid AuthorId, string Body, string ActorRole = "") : IRequest<ServiceResult<CommentDto>>;

public class AddCommentHandler : IRequestHandler<AddCommentCommand, ServiceResult<CommentDto>>
{
    private readonly ITaskCommentRepository _comments;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public AddCommentHandler(
        ITaskCommentRepository comments,
        IEngineerRepository engineers,
        IProjectAccessPolicy access,
        ITaskRepository tasks,
        IProjectRepository projects,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _comments = comments;
        _engineers = engineers;
        _access = access;
        _tasks = tasks;
        _projects = projects;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<CommentDto>> Handle(AddCommentCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return ServiceResult<CommentDto>.Fail("EMPTY_BODY", "Comment cannot be empty.");

        if (request.Body.Length > 2000)
            return ServiceResult<CommentDto>.Fail("TOO_LONG", "Comment must be 2000 characters or fewer.");

        // An Executive reads everything in their organization but writes nothing, by design (they are not in ProjectAccessPolicy's global roles —
        // widening that would give them write access everywhere). A comment is the one thing they may add, on any task except someone's private
        // to-do in a personal project. The task lookup is organization-filtered, so "any task" never reaches another organization's.
        var isExecutive = request.ActorRole == Roles.Executive;
        if (isExecutive)
        {
            var execTask = await _tasks.GetByIdAsync(request.TaskId, ct);
            if (execTask is null)
                return ServiceResult<CommentDto>.Fail("NOT_FOUND", $"Task '{request.TaskId}' not found.");
            var project = await _projects.GetByIdAsync(execTask.ProjectId, ct);
            if (project?.PersonalOwnerId is not null)
                return ServiceResult<CommentDto>.Fail("FORBIDDEN", "You do not have access to this task.");
        }
        else if (!await _access.CanAccessTaskAsync(request.TaskId, request.AuthorId, request.ActorRole, ct))
            return ServiceResult<CommentDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var author = await _engineers.GetByIdAsync(request.AuthorId, ct);
        if (author is null)
            return ServiceResult<CommentDto>.Fail("NOT_FOUND", "Author not found.");

        var comment = TaskComment.Create(request.TaskId, request.AuthorId, request.Body);
        await _comments.AddAsync(comment, ct);
        await _comments.SaveChangesAsync(ct);

        var mentionedIds = await NotifyMentionsAsync(request.TaskId, request.Body, request.AuthorId, author.Name, ct);
        if (isExecutive)
            await NotifyExecutiveCommentAsync(request.TaskId, request.Body, request.AuthorId, author.Name, mentionedIds, ct);

        return ServiceResult<CommentDto>.Ok(CommentDto.From(comment, author.Name));
    }

    /// <summary>Notifies "@Full Name" mentions found in the comment body — scoped to everyone who
    /// can access the task's project (see MentionParser / ProjectAccessPolicy.
    /// GetAccessibleEngineerIdsAsync), plus the task's own creator, who is always mentionable on
    /// their own task even when their role/team doesn't otherwise carry standing project access
    /// (e.g. a PMO engineer who filed a task for a team they aren't part of).</summary>
    private async Task<HashSet<Guid>> NotifyMentionsAsync(Guid taskId, string commentBody, Guid authorId, string authorName, CancellationToken ct)
    {
        var notified = new HashSet<Guid>();
        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task is null) return notified;

        var accessibleIds = (await _access.GetAccessibleEngineerIdsAsync(task.ProjectId, ct)).ToHashSet();
        if (task.CreatedById is Guid creatorId)
            accessibleIds.Add(creatorId);
        if (accessibleIds.Count == 0) return notified;
        var candidates = await _engineers.GetByIdsAsync(accessibleIds.ToList(), ct);

        var mentioned = MentionParser.ExtractMentions(commentBody, candidates, authorId);
        if (mentioned.Count == 0) return notified;

        var excerpt = commentBody.Length > 200 ? commentBody[..200] + "…" : commentBody;
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{taskId}";

        foreach (var engineer in mentioned)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId,
                taskTitle       = task.Title,
                reason          = excerpt,
                mentionedByName = authorName,
            });

            await _notify.NotifyAsync(engineer.Id, NotificationKind.Mentioned, payload, ct: ct);

            var emailBody = $"""
                <p>Hi {engineer.Name},</p>
                <p><strong>{authorName}</strong> mentioned you in a comment on <strong>{task.Title}</strong>:</p>
                <p style="background:#f8fafc;border-left:4px solid #6366f1;padding:12px 16px;border-radius:4px;color:#334155;">
                  {System.Net.WebUtility.HtmlEncode(excerpt)}
                </p>
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because you were mentioned in a comment.")}
                """;
            await _notify.EmailAsync(engineer.Id, NotificationKind.Mentioned, new NotificationEmail(engineer.Email, $"You were mentioned: {task.Title}", EmailTemplate.Layout(emailBody)), ct);
            notified.Add(engineer.Id);
        }

        return notified;
    }

    /// <summary>Tells the task's assignee and creator that an Executive commented — skipping the author and anyone the comment already notified by
    /// @mention, so nobody gets two alerts for one comment. Goes through the dispatcher, so each recipient's own notification preferences apply.</summary>
    private async Task NotifyExecutiveCommentAsync(
        Guid taskId, string commentBody, Guid authorId, string authorName, HashSet<Guid> alreadyNotified, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task is null) return;

        var recipientIds = new[] { task.AssigneeId, task.CreatedById }
            .Where(id => id.HasValue).Select(id => id!.Value)
            .Where(id => id != authorId && !alreadyNotified.Contains(id))
            .Distinct().ToList();
        if (recipientIds.Count == 0) return;

        var excerpt = commentBody.Length > 200 ? commentBody[..200] + "…" : commentBody;
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{taskId}";

        foreach (var recipient in await _engineers.GetByIdsAsync(recipientIds, ct))
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId,
                taskTitle       = task.Title,
                reason          = excerpt,
                mentionedByName = authorName,
            });

            var emailBody = $"""
                <p>Hi {recipient.Name},</p>
                <p><strong>{authorName}</strong> (Executive) commented on <strong>{task.Title}</strong>:</p>
                <p style="background:#f8fafc;border-left:4px solid #6366f1;padding:12px 16px;border-radius:4px;color:#334155;">
                  {System.Net.WebUtility.HtmlEncode(excerpt)}
                </p>
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("You received this because an executive commented on a task you are assigned to or created.")}
                """;

            await _notify.NotifyAsync(recipient.Id, NotificationKind.ExecutiveCommented, payload,
                new NotificationEmail(recipient.Email, $"Executive comment: {task.Title}", EmailTemplate.Layout(emailBody)), ct);
        }
    }
}
