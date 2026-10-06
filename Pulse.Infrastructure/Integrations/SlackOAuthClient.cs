using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Pulse.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Integrations;

public class SlackOAuthClient : ISlackOAuthClient
{
    private const string AccessUrl = "https://slack.com/api/oauth.v2.access";
    private readonly HttpClient _http;
    private readonly IAppSettings _settings;
    private readonly ILogger<SlackOAuthClient> _logger;

    public SlackOAuthClient(HttpClient http, IAppSettings settings, ILogger<SlackOAuthClient> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SlackOAuthResult> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AccessUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.SlackClientId}:{_settings.SlackClientSecret}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["redirect_uri"] = _settings.SlackOAuthRedirectUri ?? "",
            });

            using var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            if (json?["ok"]?.GetValue<bool>() != true)
            {
                var error = json?["error"]?.GetValue<string>() ?? $"http_{(int)response.StatusCode}";
                _logger.LogWarning("Slack oauth.v2.access failed: {Error}", error);
                return new SlackOAuthResult(false, null, null, null, null, error);
            }

            return new SlackOAuthResult(true,
                json["team"]?["id"]?.GetValue<string>(),
                json["team"]?["name"]?.GetValue<string>(),
                json["bot_user_id"]?.GetValue<string>(),
                json["access_token"]?.GetValue<string>(),
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Slack oauth.v2.access threw");
            return new SlackOAuthResult(false, null, null, null, null, "exchange_failed");
        }
    }
}
