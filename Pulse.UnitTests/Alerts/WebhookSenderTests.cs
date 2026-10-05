using System.Net;
using Pulse.Infrastructure.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Pulse.UnitTests.Alerts;

/// <summary>Captures the outgoing request instead of hitting a real network endpoint — no HTTP
/// mocking package is referenced elsewhere in this codebase, so this stays a minimal inline fake
/// rather than pulling one in for a single test file.</summary>
internal class RecordingHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(ResponseStatusCode) { Content = new StringContent("{}") };
    }
}

public class WebhookSenderTests
{
    private readonly RecordingHttpMessageHandler _handler = new();
    private readonly Mock<ILogger<WebhookSender>> _logger = new();

    private WebhookSender CreateSender() => new(new HttpClient(_handler), _logger.Object);

    [Fact]
    public async Task Sends_the_Slack_text_payload_for_a_hooks_slack_com_url()
    {
        await CreateSender().SendAsync("https://hooks.slack.com/services/x/y/z", "Pulse alert: Too many blockers", "Team X has 5 blockers.");

        _handler.LastRequestBody.Should().Contain("\"text\"");
        _handler.LastRequestBody.Should().Contain("Too many blockers");
        _handler.LastRequestBody.Should().Contain("Team X has 5 blockers.");
        _handler.LastRequestBody.Should().NotContain("MessageCard");
    }

    [Fact]
    public async Task Sends_the_Teams_MessageCard_payload_for_a_non_slack_url()
    {
        await CreateSender().SendAsync("https://outlook.office.com/webhook/abc", "Pulse alert: Too many blockers", "Team X has 5 blockers.");

        _handler.LastRequestBody.Should().Contain("\"@type\":\"MessageCard\"");
        _handler.LastRequestBody.Should().Contain("\"activityTitle\"");
        _handler.LastRequestBody.Should().Contain("Team X has 5 blockers.");
    }

    [Fact]
    public async Task Throws_when_the_webhook_endpoint_returns_a_failure_status()
    {
        _handler.ResponseStatusCode = HttpStatusCode.BadRequest;

        var act = async () => await CreateSender().SendAsync("https://hooks.slack.com/services/x", "Title", "Message");

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
