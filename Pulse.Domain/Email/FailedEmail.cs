using Pulse.Domain.Common;

namespace Pulse.Domain.Email;

public class FailedEmail : Entity
{
    public string To { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string HtmlBody { get; private set; } = string.Empty;
    public int AttemptCount { get; private set; }
    public DateTime LastAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public bool IsResolved { get; private set; }
    public DateTime? ResolvedAt { get; private set; }

    private FailedEmail() { }

    public static FailedEmail Create(string to, string subject, string htmlBody, string? error, int attemptCount) =>
        new()
        {
            To            = to,
            Subject       = subject,
            HtmlBody      = htmlBody,
            LastError     = error,
            AttemptCount  = attemptCount,
            LastAttemptAt = DateTime.UtcNow,
        };

    public void RecordRetryFailure(string? error)
    {
        AttemptCount++;
        LastAttemptAt = DateTime.UtcNow;
        LastError = error;
    }

    public void MarkResolved()
    {
        IsResolved = true;
        ResolvedAt = DateTime.UtcNow;
    }
}
