namespace Pulse.Application.Common.Interfaces;

/// <summary>Fire-and-forget delivery of an alert to a Slack or Microsoft Teams incoming webhook —
/// mirrors IEmailQueue's shape exactly. Which of the two platforms to format for is inferred from
/// the URL's host by the implementation, so callers never need to know or specify it.</summary>
public interface IWebhookNotifier
{
    void Enqueue(string webhookUrl, string title, string message);
}
