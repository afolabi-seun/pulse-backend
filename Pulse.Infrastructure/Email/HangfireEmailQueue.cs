using Pulse.Application.Common.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Email;

public class HangfireEmailQueue : IEmailQueue
{
    private readonly IBackgroundJobClient _jobs;
    private readonly IEmailService _fallback;
    private readonly ILogger<HangfireEmailQueue> _logger;

    public HangfireEmailQueue(
        IBackgroundJobClient jobs,
        IEmailService fallback,
        ILogger<HangfireEmailQueue> logger)
    {
        _jobs = jobs;
        _fallback = fallback;
        _logger = logger;
    }

    public void Enqueue(string to, string subject, string htmlBody)
    {
        try
        {
            _jobs.Enqueue<SendEmailJob>(j => j.ExecuteAsync(to, subject, htmlBody));
        }
        catch (Exception ex)
        {
            // Hangfire storage is unavailable — fall back to direct send so the email is not lost.
            // IEmailService.SendAsync has its own retry loop and will persist a FailedEmail on final failure.
            _logger.LogError(ex, "Hangfire enqueue failed for {To}; falling back to direct send", to);
            _ = _fallback.SendAsync(to, subject, htmlBody, CancellationToken.None);
        }
    }
}
