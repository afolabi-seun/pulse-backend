using System.Security.Cryptography;
using System.Text;
using Pulse.Api.Controllers;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Slack;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class SlackEventsControllerTests
{
    private const string Secret = "test-signing-secret";

    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();

    public SlackEventsControllerTests()
    {
        _settings.Setup(s => s.SlackSigningSecret).Returns(Secret);
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("1");
    }

    private SlackEventsController CreateController(string body, string? signature, string? timestamp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature is not null) ctx.Request.Headers["X-Slack-Signature"] = signature;
        if (timestamp is not null) ctx.Request.Headers["X-Slack-Request-Timestamp"] = timestamp;

        return new SlackEventsController(_settings.Object, _jobs.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = ctx },
        };
    }

    private static string ValidTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    private static string Sign(string secret, string timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private const string UrlVerificationBody = """{"type":"url_verification","challenge":"abc123","token":"xyz"}""";

    private static string EventCallbackBody(string eventJson) => $$"""{"type":"event_callback","event":{{eventJson}}}""";

    private const string ThreadedReplyEvent = """{"type":"message","channel":"C123","text":"why though?","thread_ts":"1700.001","ts":"1700.002"}""";
    private const string BotMessageEvent = """{"type":"message","channel":"C123","text":"an alert fired","thread_ts":"1700.001","ts":"1700.002","bot_id":"B999"}""";
    private const string TopLevelMessageEvent = """{"type":"message","channel":"C123","text":"hello","ts":"1700.002"}""";
    private const string EditedMessageEvent = """{"type":"message","channel":"C123","text":"edited","thread_ts":"1700.001","ts":"1700.002","subtype":"message_changed"}""";

    [Fact]
    public async Task Rejects_a_request_with_no_signature_headers()
    {
        var result = await CreateController(UrlVerificationBody, null, null).Receive(default);

        result.Should().BeOfType<UnauthorizedResult>();
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task Rejects_a_request_with_an_incorrect_signature()
    {
        var timestamp = ValidTimestamp();
        var result = await CreateController(UrlVerificationBody, "v0=wrong", timestamp).Receive(default);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Rejects_a_stale_request_even_with_a_correctly_computed_signature()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var signature = Sign(Secret, timestamp, UrlVerificationBody);

        var result = await CreateController(UrlVerificationBody, signature, timestamp).Receive(default);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Rejects_every_request_when_no_signing_secret_is_configured()
    {
        _settings.Setup(s => s.SlackSigningSecret).Returns((string?)null);
        var timestamp = ValidTimestamp();
        var signature = Sign(Secret, timestamp, UrlVerificationBody);

        var result = await CreateController(UrlVerificationBody, signature, timestamp).Receive(default);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Echoes_the_challenge_for_the_url_verification_handshake()
    {
        var timestamp = ValidTimestamp();
        var signature = Sign(Secret, timestamp, UrlVerificationBody);

        var result = await CreateController(UrlVerificationBody, signature, timestamp).Receive(default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.Content.Should().Be("abc123");
        content.ContentType.Should().Contain("text/plain");
    }

    [Fact]
    public async Task Enqueues_the_reply_job_for_a_genuine_threaded_reply()
    {
        var body = EventCallbackBody(ThreadedReplyEvent);
        var timestamp = ValidTimestamp();
        var signature = Sign(Secret, timestamp, body);

        var result = await CreateController(body, signature, timestamp).Receive(default);

        result.Should().BeOfType<OkResult>();
        _jobs.Verify(j => j.Create(
            It.Is<Job>(job => job.Method.Name == nameof(HandleSlackReplyJob.ExecuteAsync)
                && (string)job.Args[0] == "C123"
                && (string)job.Args[1] == "1700.001"
                && (string)job.Args[2] == "why though?"),
            It.IsAny<IState>()), Times.Once);
    }

    [Theory]
    [InlineData(BotMessageEvent)] // Pulse's own posted alert — must not answer itself
    [InlineData(TopLevelMessageEvent)] // not a reply to any alert thread
    [InlineData(EditedMessageEvent)] // an edit/subtype, not a genuine new reply
    public async Task Ignores_events_that_are_not_a_genuine_threaded_human_reply(string eventJson)
    {
        var body = EventCallbackBody(eventJson);
        var timestamp = ValidTimestamp();
        var signature = Sign(Secret, timestamp, body);

        var result = await CreateController(body, signature, timestamp).Receive(default);

        result.Should().BeOfType<OkResult>();
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }
}
