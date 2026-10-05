using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pulse.Application.Alerts;
using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Tasks;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.AlertExplanations;

/// <summary>Drafts a plain-language explanation for a fired AlertRule by giving Claude a small set
/// of read-only tools pre-scoped to the rule's own team/project — the model can't ask for a
/// different team's data, and none of the tools can mutate anything. AlertRuleScanner has already
/// decided the rule fired; this class only explains why, never re-decides whether it should have.
///
/// Deliberately hand-rolled against the raw Messages API (no SDK dependency) — the tool-use loop is
/// small enough that a minimal client is less risk than a new package dependency for one feature.</summary>
public class AnthropicAlertExplainer : IAlertExplainer
{
    private const int MaxToolRounds = 4;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly IAppSettings _settings;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectRepository _projects;
    private readonly ICheckInRepository _checkIns;
    private readonly ILogger<AnthropicAlertExplainer> _logger;

    public AnthropicAlertExplainer(
        HttpClient http, IAppSettings settings, ITaskRepository tasks, IEngineerRepository engineers,
        IProjectRepository projects, ICheckInRepository checkIns, ILogger<AnthropicAlertExplainer> logger)
    {
        _http = http;
        _settings = settings;
        _tasks = tasks;
        _engineers = engineers;
        _projects = projects;
        _checkIns = checkIns;
        _logger = logger;
    }

    public Task<string?> ExplainAsync(AlertRule rule, double value, string scopeName, CancellationToken ct = default)
    {
        var system = """
            You are Pulse's alerting assistant. A metric an engineering manager is watching just
            crossed a threshold they configured. Explain what happened in 2-4 short sentences, in
            plain language, grounded in the actual data your tools return — never guess or invent
            specifics. Use tools to find out what's actually going on before answering. Name specific
            tasks, people, or numbers when they help; don't just restate the metric and threshold back.
            Keep it factual and calm — this is an FYI for someone busy, not an incident report.
            """;

        var userMessage = $"""
            Alert "{rule.Name}" fired: {scopeName}'s {AlertMetricFormatting.MetricLabel(rule.Metric)} is {AlertMetricFormatting.FormatValue(rule.Metric, value)},
            which is {(rule.Comparator == AlertComparator.GreaterThan ? "above" : "below")} the threshold of {AlertMetricFormatting.FormatValue(rule.Metric, rule.Threshold)}.
            Scope: {rule.ScopeType} "{scopeName}".
            """;

        return RunAgentLoopAsync(system, userMessage, new ToolContext(rule.ScopeType, rule.ScopeId), ct);
    }

    public Task<string?> AnswerFollowUpAsync(AlertRule rule, string followUpQuestion, CancellationToken ct = default)
    {
        var system = """
            You are Pulse's alerting assistant, continuing a conversation about an alert you
            already explained once. Answer the follow-up question in 2-4 short sentences, grounded in
            your tools' actual data — never guess or invent specifics. This is a fresh lookup, not a
            memory of the earlier exchange, so re-check the current data rather than assuming nothing
            changed.
            """;

        var userMessage = $"""
            This is a follow-up about the alert rule "{rule.Name}", which watches {AlertMetricFormatting.MetricLabel(rule.Metric)}
            on {rule.ScopeType} scope. The follow-up question is: {followUpQuestion}
            """;

        return RunAgentLoopAsync(system, userMessage, new ToolContext(rule.ScopeType, rule.ScopeId), ct);
    }

    private record ToolContext(AlertScopeType ScopeType, Guid ScopeId);

    private async Task<string?> RunAgentLoopAsync(string system, string userMessage, ToolContext toolContext, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.AnthropicApiKey))
            return null;

        try
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userMessage },
            };

            for (var round = 0; round < MaxToolRounds; round++)
            {
                var requestBody = new JsonObject
                {
                    ["model"] = _settings.AnthropicModel,
                    ["max_tokens"] = 1024,
                    ["system"] = system,
                    ["messages"] = messages.DeepClone(),
                    ["tools"] = BuildToolDefinitions(),
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
                request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
                request.Headers.Add("anthropic-version", ApiVersion);
                request.Content = JsonContent.Create(requestBody);

                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Anthropic API call failed with {Status}: {Body}", response.StatusCode, errorBody);
                    return null;
                }

                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
                var contentBlocks = responseJson?["content"]?.AsArray();
                var stopReason = responseJson?["stop_reason"]?.GetValue<string>();
                if (contentBlocks is null)
                    return null;

                if (stopReason != "tool_use")
                {
                    var text = string.Concat(contentBlocks
                        .Where(b => b?["type"]?.GetValue<string>() == "text")
                        .Select(b => b!["text"]!.GetValue<string>()));
                    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                }

                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = contentBlocks.DeepClone() });

                var toolResults = new JsonArray();
                foreach (var block in contentBlocks)
                {
                    if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                    var toolUseId = block["id"]!.GetValue<string>();
                    var toolName = block["name"]!.GetValue<string>();
                    var result = await ExecuteToolAsync(toolName, toolContext, ct);
                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = result,
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }

            _logger.LogWarning("Alert explainer did not converge within {MaxRounds} tool rounds", MaxToolRounds);
            return null;
        }
        catch (Exception ex)
        {
            // Never let a drafting failure block the alert itself — the caller falls back to its
            // own plain templated message.
            _logger.LogWarning(ex, "Alert explainer failed; falling back to the plain templated message");
            return null;
        }
    }

    // ── Tools — each pre-scoped by closure to the firing rule's own team/project; the model never
    // supplies a scope, so it cannot ask for another team's or project's data. ─────────────────────

    private static JsonArray BuildToolDefinitions() => new()
    {
        Tool("list_blocked_tasks", "Lists currently blocked tasks in scope, with assignee, days blocked, and reason."),
        Tool("get_team_velocity_history", "Story points delivered per week over the last 6 weeks (team scope only)."),
        Tool("get_qa_reject_details", "Per-engineer QA submissions and rejections over the trailing 30 days (project scope only)."),
        Tool("get_check_in_gaps", "Names of engineers on the team who have not checked in this week (team scope only)."),
    };

    private static JsonObject Tool(string name, string description) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    private async Task<string> ExecuteToolAsync(string toolName, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "list_blocked_tasks" => await ListBlockedTasksAsync(ctx, ct),
                "get_team_velocity_history" => ctx.ScopeType == AlertScopeType.Team
                    ? await GetTeamVelocityHistoryAsync(ctx.ScopeId, ct)
                    : ScopeMismatch("team"),
                "get_qa_reject_details" => ctx.ScopeType == AlertScopeType.Project
                    ? await GetQaRejectDetailsAsync(ctx.ScopeId, ct)
                    : ScopeMismatch("project"),
                "get_check_in_gaps" => ctx.ScopeType == AlertScopeType.Team
                    ? await GetCheckInGapsAsync(ctx.ScopeId, ct)
                    : ScopeMismatch("team"),
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool '{toolName}'." }),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert explainer tool {Tool} failed", toolName);
            return JsonSerializer.Serialize(new { error = "This tool failed to run." });
        }
    }

    private static string ScopeMismatch(string expectedScope) =>
        JsonSerializer.Serialize(new { error = $"This alert is not scoped to a {expectedScope}, so this tool has no data." });

    private async Task<string> ListBlockedTasksAsync(ToolContext ctx, CancellationToken ct)
    {
        var blocked = await _tasks.GetBlockedTasksAsync(ct);
        IEnumerable<PulseTask> scoped;
        if (ctx.ScopeType == AlertScopeType.Project)
        {
            scoped = blocked.Where(t => t.ProjectId == ctx.ScopeId);
        }
        else
        {
            var teamEngineerIds = (await _engineers.ListAllAsync(ct))
                .Where(e => e.TeamId == ctx.ScopeId).Select(e => e.Id).ToHashSet();
            scoped = blocked.Where(t => t.AssigneeId.HasValue && teamEngineerIds.Contains(t.AssigneeId.Value));
        }

        var scopedList = scoped.Take(10).ToList();
        var engineerIds = scopedList.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value).Distinct().ToList();
        var namesById = engineerIds.Count > 0
            ? (await _engineers.GetByIdsAsync(engineerIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var items = scopedList.Select(t => new
        {
            title = t.Title,
            assignee = t.AssigneeId.HasValue && namesById.TryGetValue(t.AssigneeId.Value, out var name) ? name : "Unassigned",
            // Mirrors GetPmoReportQuery's own "days blocked" computation (now - ActivatedAt) for consistency.
            daysBlocked = (int)(DateTime.UtcNow - t.ActivatedAt).TotalDays,
            reason = t.BlockerReason,
        });
        return JsonSerializer.Serialize(items);
    }

    private async Task<string> GetTeamVelocityHistoryAsync(Guid teamId, CancellationToken ct)
    {
        var points = await _tasks.GetWeeklyThroughputByTeamAsync(teamId, ct);
        var items = points.OrderBy(p => p.WeekOf)
            .Select(p => new { weekOf = p.WeekOf.ToString("yyyy-MM-dd"), pointsDelivered = p.PointsDelivered });
        return JsonSerializer.Serialize(items);
    }

    private async Task<string> GetQaRejectDetailsAsync(Guid projectId, CancellationToken ct)
    {
        var members = await _projects.ListMembersAsync(projectId, ct);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-30);

        var items = new List<object>();
        foreach (var member in members)
        {
            var stats = await _tasks.GetPerformanceStatsAsync(member.EngineerId, from, to, projectId, ct);
            if (stats.TasksSentToQa > 0)
                items.Add(new { engineer = member.Name, tasksSentToQa = stats.TasksSentToQa, tasksQaRejected = stats.TasksQaRejected });
        }
        return JsonSerializer.Serialize(items);
    }

    private async Task<string> GetCheckInGapsAsync(Guid teamId, CancellationToken ct)
    {
        var roster = (await _engineers.ListAllAsync(ct))
            .Where(e => e.TeamId == teamId && e.IsActive
                && CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles.Contains(e.Role))
            .ToList();
        if (roster.Count == 0)
            return JsonSerializer.Serialize(new { message = "No one on this team is expected to check in." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var counts = await _checkIns.GetCheckInCountByDateRangeAsync(weekStart, today, ct);
        var missing = roster.Where(e => !counts.TryGetValue(e.Id, out var c) || c == 0).Select(e => e.Name);
        return JsonSerializer.Serialize(new { missingCheckIns = missing });
    }

}
