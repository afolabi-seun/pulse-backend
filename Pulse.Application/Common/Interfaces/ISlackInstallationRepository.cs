using Pulse.Domain.Integrations;

namespace Pulse.Application.Common.Interfaces;

public interface ISlackInstallationRepository
{
    Task<SlackInstallation?> GetByOrganizationAsync(Guid organizationId, CancellationToken ct = default);
    /// <summary>Which organization a Slack workspace belongs to. Only meaningful with no caller org
    /// (the anonymous OAuth callback and events endpoint, background jobs) — otherwise org-filtered.</summary>
    Task<SlackInstallation?> GetByTeamIdAsync(string teamId, CancellationToken ct = default);
    Task AddAsync(SlackInstallation installation, CancellationToken ct = default);
    void Remove(SlackInstallation installation);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The bot token to post with, for the organization the current request or job is acting for.</summary>
public interface ISlackTokenProvider
{
    /// <summary>The org's own workspace token; for the default organization without one, the legacy
    /// SLACK_BOT_TOKEN; otherwise null (that org hasn't connected Slack).</summary>
    Task<string?> GetBotTokenAsync(CancellationToken ct = default);
}

/// <summary>Slack's OAuth v2 code exchange (oauth.v2.access).</summary>
public interface ISlackOAuthClient
{
    Task<SlackOAuthResult> ExchangeCodeAsync(string code, CancellationToken ct = default);
}

public record SlackOAuthResult(bool Ok, string? TeamId, string? TeamName, string? BotUserId, string? BotToken, string? Error);
