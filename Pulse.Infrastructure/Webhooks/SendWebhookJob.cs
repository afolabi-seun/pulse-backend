using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.Webhooks;

/// <summary>Hangfire job that delivers a single webhook alert. Unlike SendEmailJob, this has no
/// inner retry loop of its own — Hangfire's own default automatic-retry policy covers a transient
/// failure, which is proportionate for a best-effort ops alert (unlike email, nothing here needs a
/// persisted dead-letter record for someone to follow up on later).</summary>
public class SendWebhookJob
{
    private readonly IWebhookSender _sender;

    public SendWebhookJob(IWebhookSender sender) => _sender = sender;

    public Task ExecuteAsync(string webhookUrl, string title, string message) =>
        _sender.SendAsync(webhookUrl, title, message);
}
