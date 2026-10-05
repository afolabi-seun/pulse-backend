using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pulse.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Slack;

public class SlackClient : ISlackClient
{
    private const string PostMessageUrl = "https://slack.com/api/chat.postMessage";

    private readonly HttpClient _http;
    private readonly IAppSettings _settings;
    private readonly ILogger<SlackClient> _logger;

    public SlackClient(HttpClient http, IAppSettings settings, ILogger<SlackClient> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<(string ChannelId, string Ts)?> PostMessageAsync(
        string channel, string text, string? threadTs = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_settings.SlackBotToken))
            return null;

        try
        {
            var payload = new Dictionary<string, string?> { ["channel"] = channel, ["text"] = text };
            if (threadTs is not null)
                payload["thread_ts"] = threadTs;

            using var request = new HttpRequestMessage(HttpMethod.Post, PostMessageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SlackBotToken);
            request.Content = JsonContent.Create(payload);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Slack chat.postMessage HTTP failure: {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            var ok = json?["ok"]?.GetValue<bool>() ?? false;
            if (!ok)
            {
                _logger.LogWarning("Slack chat.postMessage rejected: {Error}", json?["error"]?.GetValue<string>() ?? "unknown");
                return null;
            }

            return (json!["channel"]!.GetValue<string>(), json["ts"]!.GetValue<string>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slack chat.postMessage threw");
            return null;
        }
    }
}
