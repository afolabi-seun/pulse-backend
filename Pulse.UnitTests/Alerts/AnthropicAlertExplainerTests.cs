using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Infrastructure.AlertExplanations;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Pulse.UnitTests.Alerts;

/// <summary>Replays a queue of canned Anthropic API responses, one per call, and records every
/// outgoing request body — lets a test assert on both what was sent (e.g. that a tool_result made
/// it into the follow-up request) and what came back, without a real network call.</summary>
internal class QueuedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    public List<string> RequestBodies { get; } = [];

    public void Enqueue(string responseJson, HttpStatusCode status = HttpStatusCode.OK) =>
        _responses.Enqueue((status, responseJson));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

public class AnthropicAlertExplainerTests
{
    private readonly QueuedHttpMessageHandler _handler = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<ILogger<AnthropicAlertExplainer>> _logger = new();

    public AnthropicAlertExplainerTests()
    {
        _settings.Setup(s => s.AnthropicApiKey).Returns("test-key");
        _settings.Setup(s => s.AnthropicModel).Returns("claude-sonnet-5");
    }

    private AnthropicAlertExplainer CreateExplainer() =>
        new(new HttpClient(_handler), _settings.Object, _tasks.Object, _engineers.Object, _projects.Object, _checkIns.Object, _logger.Object);

    private static AlertRule NewRule() =>
        AlertRule.Create(Guid.NewGuid(), "Too many blockers", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);

    private const string TextOnlyResponse = """
        {"content":[{"type":"text","text":"Team X has 6 open blockers, well above the threshold."}],"stop_reason":"end_turn"}
        """;

    [Fact]
    public async Task Returns_null_immediately_without_any_HTTP_call_when_no_api_key_is_configured()
    {
        _settings.Setup(s => s.AnthropicApiKey).Returns((string?)null);

        var result = await CreateExplainer().ExplainAsync(NewRule(), 9, "Team X", default);

        result.Should().BeNull();
        _handler.RequestBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_the_final_text_when_the_model_answers_without_using_a_tool()
    {
        _handler.Enqueue(TextOnlyResponse);

        var result = await CreateExplainer().ExplainAsync(NewRule(), 9, "Team X", default);

        result.Should().Be("Team X has 6 open blockers, well above the threshold.");
    }

    [Fact]
    public async Task Executes_a_requested_tool_and_sends_its_result_back_before_the_final_answer()
    {
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync([]);

        var toolUseResponse = """
            {"content":[{"type":"tool_use","id":"toolu_1","name":"list_blocked_tasks","input":{}}],"stop_reason":"tool_use"}
            """;
        _handler.Enqueue(toolUseResponse);
        _handler.Enqueue(TextOnlyResponse);

        var result = await CreateExplainer().ExplainAsync(NewRule(), 9, "Team X", default);

        result.Should().Be("Team X has 6 open blockers, well above the threshold.");
        _handler.RequestBodies.Should().HaveCount(2);
        _handler.RequestBodies[1].Should().Contain("tool_result");
        _handler.RequestBodies[1].Should().Contain("toolu_1");
    }

    [Fact]
    public async Task Returns_null_when_the_api_call_fails()
    {
        _handler.Enqueue("{\"error\":\"bad request\"}", HttpStatusCode.BadRequest);

        var result = await CreateExplainer().ExplainAsync(NewRule(), 9, "Team X", default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_model_never_stops_requesting_tools()
    {
        var toolUseResponse = """
            {"content":[{"type":"tool_use","id":"toolu_x","name":"list_blocked_tasks","input":{}}],"stop_reason":"tool_use"}
            """;
        for (var i = 0; i < 10; i++) _handler.Enqueue(toolUseResponse);
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync([]);

        var result = await CreateExplainer().ExplainAsync(NewRule(), 9, "Team X", default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AnswerFollowUpAsync_also_returns_null_without_an_api_key()
    {
        _settings.Setup(s => s.AnthropicApiKey).Returns((string?)null);

        var result = await CreateExplainer().AnswerFollowUpAsync(NewRule(), "Why is this blocked?", default);

        result.Should().BeNull();
    }
}
