using Pulse.Application.Common.Interfaces;
using Hangfire;

namespace Pulse.Infrastructure.Email;

/// <summary>
/// Hangfire job that delivers a single email. Uses IEmailService which handles
/// its own retry (3 attempts, exponential backoff) and persists failures to the
/// failed_emails table on final defeat. Marked no-retry at the Hangfire level
/// because the inner service already manages the retry loop.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public class SendEmailJob
{
    private readonly IEmailService _email;

    public SendEmailJob(IEmailService email) => _email = email;

    public async Task ExecuteAsync(string to, string subject, string htmlBody)
    {
        await _email.SendAsync(to, subject, htmlBody);
    }
}
