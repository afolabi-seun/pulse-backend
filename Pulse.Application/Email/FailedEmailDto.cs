using Pulse.Domain.Email;

namespace Pulse.Application.Email;

public record FailedEmailDto(
    Guid Id,
    string To,
    string Subject,
    int AttemptCount,
    DateTime LastAttemptAt,
    string? LastError,
    bool IsResolved,
    DateTime? ResolvedAt,
    DateTime CreatedAt)
{
    public static FailedEmailDto From(FailedEmail e) =>
        new(e.Id, e.To, e.Subject, e.AttemptCount, e.LastAttemptAt, e.LastError, e.IsResolved, e.ResolvedAt, e.CreatedAt);
}

public record FailedEmailPageDto(IReadOnlyList<FailedEmailDto> Items, Guid? NextCursor, bool HasMore);
