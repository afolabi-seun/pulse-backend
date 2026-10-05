using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Pulse.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Webhooks;

/// <summary>Posts an alert to a Slack or Microsoft Teams incoming webhook. The two platforms use
/// incompatible payload shapes (Slack: {"text": ...}; Teams' legacy "Incoming Webhook" connector:
/// a MessageCard), so which one to send is inferred from the URL's host — hooks.slack.com is Slack,
/// anything else is treated as Teams. This covers the two named platforms without asking the user
/// to also pick one in the UI.</summary>
public class WebhookSender : IWebhookSender
{
    private readonly HttpClient _http;
    private readonly ILogger<WebhookSender> _logger;

    public WebhookSender(HttpClient http, ILogger<WebhookSender> logger)
    {
        _http = http;
        _logger = logger;
    }

    private record SlackPayload([property: JsonPropertyName("text")] string Text);

    private record TeamsSection(
        [property: JsonPropertyName("activityTitle")] string ActivityTitle,
        [property: JsonPropertyName("text")] string Text);

    private record TeamsPayload(
        [property: JsonPropertyName("@type")] string Type,
        [property: JsonPropertyName("@context")] string Context,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("themeColor")] string ThemeColor,
        [property: JsonPropertyName("sections")] TeamsSection[] Sections);

    public async Task SendAsync(string webhookUrl, string title, string message, CancellationToken ct = default)
    {
        var isSlack = webhookUrl.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase);

        var response = isSlack
            ? await _http.PostAsJsonAsync(webhookUrl, new SlackPayload($"*{title}*\n{message}"), ct)
            : await _http.PostAsJsonAsync(webhookUrl,
                new TeamsPayload("MessageCard", "http://schema.org/extensions", title, "6366F1",
                    [new TeamsSection(title, message)]), ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Webhook delivery to {Host} failed with {Status}: {Body}",
                new Uri(webhookUrl).Host, response.StatusCode, body);
            throw new HttpRequestException($"Webhook delivery failed with status {response.StatusCode}");
        }
    }
}
