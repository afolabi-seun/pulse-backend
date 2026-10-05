using Pulse.Application.Common.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Webhooks;

public class HangfireWebhookNotifier : IWebhookNotifier
{
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<HangfireWebhookNotifier> _logger;

    public HangfireWebhookNotifier(IBackgroundJobClient jobs, ILogger<HangfireWebhookNotifier> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public void Enqueue(string webhookUrl, string title, string message)
    {
        try
        {
            _jobs.Enqueue<SendWebhookJob>(j => j.ExecuteAsync(webhookUrl, title, message));
        }
        catch (Exception ex)
        {
            // Hangfire storage is unavailable — this alert is lost, but unlike email there's no
            // direct-send fallback worth building for a best-effort ops notification.
            _logger.LogError(ex, "Hangfire enqueue failed for webhook delivery to {Host}", new Uri(webhookUrl).Host);
        }
    }
}
