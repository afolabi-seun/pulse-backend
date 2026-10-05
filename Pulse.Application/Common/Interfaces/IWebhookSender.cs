namespace Pulse.Application.Common.Interfaces;

/// <summary>Does the actual HTTP POST to a webhook URL — the counterpart to IEmailService for the
/// webhook channel. IWebhookNotifier.Enqueue defers to this via a Hangfire job, the same split
/// IEmailQueue/IEmailService already use for email.</summary>
public interface IWebhookSender
{
    Task SendAsync(string webhookUrl, string title, string message, CancellationToken ct = default);
}
