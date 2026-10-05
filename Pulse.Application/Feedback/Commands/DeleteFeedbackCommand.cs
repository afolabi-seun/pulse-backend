using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Feedback.Commands;

public record DeleteFeedbackCommand(Guid FeedbackId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<Unit>>;

public class DeleteFeedbackHandler : IRequestHandler<DeleteFeedbackCommand, ServiceResult<Unit>>
{
    private readonly IFeedbackRepository _feedback;
    private readonly IAuditLogRepository _auditLog;

    public DeleteFeedbackHandler(IFeedbackRepository feedback, IAuditLogRepository auditLog)
    {
        _feedback = feedback;
        _auditLog = auditLog;
    }

    public async Task<ServiceResult<Unit>> Handle(DeleteFeedbackCommand command, CancellationToken ct)
    {
        var entry = await _feedback.GetByIdAsync(command.FeedbackId, ct);
        if (entry is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Feedback entry not found.");

        if (entry.EngineerId != command.ActorId)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You can only delete your own feedback.");

        // Hash the content before deletion so the audit record proves what was removed
        // without retaining the actual text.
        var contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(entry.Text)));

        var payload = JsonSerializer.Serialize(new
        {
            feedbackId = entry.Id,
            weekOf = entry.WeekOf.ToString("O"),
            contentHash,
        });

        await _feedback.DeleteAsync(entry, ct);
        await _feedback.SaveChangesAsync(ct);

        await _auditLog.LogAsync("FEEDBACK_DELETED", command.ActorId, command.IpAddress, payload, ct);

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
