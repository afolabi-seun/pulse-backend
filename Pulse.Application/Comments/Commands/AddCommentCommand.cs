using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
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
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public AddCommentHandler(
        ITaskCommentRepository comments,
        IEngineerRepository engineers,
        IProjectAccessPolicy access,
        ITaskRepository tasks,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _comments = comments;
        _engineers = engineers;
        _access = access;
        _tasks = tasks;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<CommentDto>> Handle(AddCommentCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return ServiceResult<CommentDto>.Fail("EMPTY_BODY", "Comment cannot be empty.");

        if (request.Body.Length > 2000)
            return ServiceResult<CommentDto>.Fail("TOO_LONG", "Comment must be 2000 characters or fewer.");

        if (!await _access.CanAccessTaskAsync(request.TaskId, request.AuthorId, request.ActorRole, ct))
            return ServiceResult<CommentDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var author = await _engineers.GetByIdAsync(request.AuthorId, ct);
        if (author is null)
            return ServiceResult<CommentDto>.Fail("NOT_FOUND", "Author not found.");

        var comment = TaskComment.Create(request.TaskId, request.AuthorId, request.Body);
        await _comments.AddAsync(comment, ct);
        await _comments.SaveChangesAsync(ct);

        await NotifyMentionsAsync(request.TaskId, request.Body, request.AuthorId, author.Name, ct);

        return ServiceResult<CommentDto>.Ok(CommentDto.From(comment, author.Name));
    }

    /// <summary>Notifies "@Full Name" mentions found in the comment body — scoped to everyone who
    /// can access the task's project (see MentionParser / ProjectAccessPolicy.
    /// GetAccessibleEngineerIdsAsync), plus the task's own creator, who is always mentionable on
    /// their own task even when their role/team doesn't otherwise carry standing project access
    /// (e.g. a PMO engineer who filed a task for a team they aren't part of).</summary>
    private async Task NotifyMentionsAsync(Guid taskId, string commentBody, Guid authorId, string authorName, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task is null) return;

        var accessibleIds = (await _access.GetAccessibleEngineerIdsAsync(task.ProjectId, ct)).ToHashSet();
        if (task.CreatedById is Guid creatorId)
            accessibleIds.Add(creatorId);
        if (accessibleIds.Count == 0) return;
        var candidates = await _engineers.GetByIdsAsync(accessibleIds.ToList(), ct);

        var mentioned = MentionParser.ExtractMentions(commentBody, candidates, authorId);
        if (mentioned.Count == 0) return;

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
        }
    }
}
