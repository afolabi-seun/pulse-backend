using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using FluentValidation;
using MediatR;

namespace Pulse.Application.Feedback.Commands;

/// <summary>A department head's private reply to a feedback entry — delivered to the submitter as
/// a notification (in-app + email), never a visible thread other engineers or heads outside the
/// department can see. Deliberately not open to HR: HR's read access is org-wide and meant to stay
/// anonymized (see ListFeedbackHandler) — letting HR reply would mean HR acting on a specific
/// person's identity, which defeats that.</summary>
public record ReplyToFeedbackCommand(Guid FeedbackId, string Text, Guid ActorId, string ActorRole, string? IpAddress) : IRequest<ServiceResult<FeedbackDto>>;

public class ReplyToFeedbackValidator : AbstractValidator<ReplyToFeedbackCommand>
{
    public ReplyToFeedbackValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(5000);
    }
}

public class ReplyToFeedbackHandler : IRequestHandler<ReplyToFeedbackCommand, ServiceResult<FeedbackDto>>
{
    private readonly IFeedbackRepository _feedback;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;
    private readonly IAuditLogRepository _auditLog;

    public ReplyToFeedbackHandler(
        IFeedbackRepository feedback, IEngineerRepository engineers, ITeamRepository teams,
        INotificationRepository notifications, IRealtimeNotifier realtime, IEmailQueue emailQueue,
        IAppSettings settings, IAuditLogRepository auditLog)
    {
        _feedback = feedback;
        _engineers = engineers;
        _teams = teams;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
        _auditLog = auditLog;
    }

    public async Task<ServiceResult<FeedbackDto>> Handle(ReplyToFeedbackCommand cmd, CancellationToken ct)
    {
        var entry = await _feedback.GetByIdAsync(cmd.FeedbackId, ct);
        if (entry is null)
            return ServiceResult<FeedbackDto>.Fail("NOT_FOUND", "Feedback entry not found.");

        // Same department scoping ListFeedbackHandler uses to decide what a head can even see —
        // replying is gated identically, so a head can never reply to feedback outside their own
        // inbox view.
        if (cmd.ActorRole is not Roles.HeadOfPmo and not Roles.ProjectManager)
        {
            var actor = await _engineers.GetByIdAsync(cmd.ActorId, ct);
            var actorDept = actor?.TeamId is Guid actorTeamId ? (await _teams.GetByIdAsync(actorTeamId, ct))?.Department : null;

            var submitter = await _engineers.GetByIdAsync(entry.EngineerId, ct);
            var submitterDept = submitter?.TeamId is Guid submitterTeamId ? (await _teams.GetByIdAsync(submitterTeamId, ct))?.Department : null;

            if (actorDept is null || actorDept != submitterDept)
                return ServiceResult<FeedbackDto>.Fail("FORBIDDEN", "You can only reply to feedback from your own department.");
        }

        try
        {
            entry.Reply(cmd.Text, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<FeedbackDto>.Fail("VALIDATION_ERROR", ex.Message);
        }

        await _feedback.SaveChangesAsync(ct);

        await _auditLog.LogAsync("FEEDBACK_REPLIED", cmd.ActorId, cmd.IpAddress,
            $"Replied to feedback {entry.Id} submitted by {entry.EngineerId}", ct);

        var replier = await _engineers.GetByIdAsync(cmd.ActorId, ct);
        var payload = JsonSerializer.Serialize(new { feedbackId = entry.Id, repliedByName = replier?.Name, replyText = cmd.Text });
        var n = Notification.Create(entry.EngineerId, NotificationKind.FeedbackReplied, payload, NotificationChannel.InApp);
        await _notifications.AddAsync(n, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(entry.EngineerId, NotificationDto.From(n), ct);

        var submitterEngineer = await _engineers.GetByIdAsync(entry.EngineerId, ct);
        if (submitterEngineer is not null)
        {
            var feedbackLink = $"{_settings.AppBaseUrl}/feedback";
            var body = $"""
                <p>Hi {submitterEngineer.Name},</p>
                <p><strong>{replier?.Name ?? "Your department head"}</strong> replied to feedback you submitted:</p>
                <p style="background:#f3f4f6;border-left:4px solid #6366f1;padding:12px 16px;border-radius:4px;color:#374151;">
                  {System.Net.WebUtility.HtmlEncode(cmd.Text)}
                </p>
                {EmailTemplate.Button(feedbackLink, "Submit more feedback")}
                {EmailTemplate.Muted("This reply is private — only you and department heads who can see this feedback know about it.")}
                """;
            _emailQueue.Enqueue(submitterEngineer.Email, "You received a reply to your feedback", EmailTemplate.Layout(body));
        }

        return ServiceResult<FeedbackDto>.Ok(FeedbackDto.From(entry, replier?.Name));
    }
}
