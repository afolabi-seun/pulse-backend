using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Integrations;
using MediatR;

namespace Pulse.Application.Integrations.Slack;

/// <summary>Per-organization Slack (multi-tenancy Phase 2b): an org's head installs Pulse into the org's own
/// Slack workspace via OAuth; alerts and follow-up replies then go through that workspace's bot token.</summary>
public static class SlackIntegrationConfig
{
    /// <summary>Bot scopes requested at install: post alerts (incl. public channels the bot hasn't joined),
    /// receive threaded replies in public and private channels, and find people by email to send them their
    /// personal notifications as direct messages.</summary>
    public const string BotScopes = "chat:write,chat:write.public,channels:history,groups:history,users:read.email";

    public static bool IsConfigured(IAppSettings settings, ISecretProtector protector) =>
        !string.IsNullOrEmpty(settings.SlackClientId)
        && !string.IsNullOrEmpty(settings.SlackClientSecret)
        && !string.IsNullOrEmpty(settings.SlackOAuthRedirectUri)
        && protector.IsConfigured;
}

public record SlackConnectionDto(bool Available, bool Connected, string? TeamName, DateTime? ConnectedAt);

// ── Status ───────────────────────────────────────────────────────────────────

public record GetSlackConnectionQuery : IRequest<ServiceResult<SlackConnectionDto>>;

public class GetSlackConnectionHandler(ICurrentUserService currentUser, ISlackInstallationRepository installations,
    IAppSettings settings, ISecretProtector protector) : IRequestHandler<GetSlackConnectionQuery, ServiceResult<SlackConnectionDto>>
{
    public async Task<ServiceResult<SlackConnectionDto>> Handle(GetSlackConnectionQuery query, CancellationToken ct)
    {
        var installation = currentUser.OrganizationId is Guid orgId ? await installations.GetByOrganizationAsync(orgId, ct) : null;
        return ServiceResult<SlackConnectionDto>.Ok(new SlackConnectionDto(
            SlackIntegrationConfig.IsConfigured(settings, protector), installation is not null,
            installation?.TeamName, installation?.CreatedAt));
    }
}

// ── Start install ────────────────────────────────────────────────────────────

public record GetSlackInstallUrlQuery(Guid ActorId) : IRequest<ServiceResult<string>>;

public class GetSlackInstallUrlHandler(ICurrentUserService currentUser, IAppSettings settings, ISecretProtector protector)
    : IRequestHandler<GetSlackInstallUrlQuery, ServiceResult<string>>
{
    public Task<ServiceResult<string>> Handle(GetSlackInstallUrlQuery query, CancellationToken ct)
    {
        if (!SlackIntegrationConfig.IsConfigured(settings, protector))
            return Task.FromResult(ServiceResult<string>.Fail("BUSINESS_RULE_VIOLATION", "Slack isn't set up on this Pulse server."));
        if (currentUser.OrganizationId is not Guid orgId)
            return Task.FromResult(ServiceResult<string>.Fail("FORBIDDEN", "No organization on this session."));

        var state = SlackInstallState.Create(orgId, query.ActorId, DateTimeOffset.UtcNow, settings.JwtSecretKey);
        var url = "https://slack.com/oauth/v2/authorize"
            + $"?client_id={Uri.EscapeDataString(settings.SlackClientId!)}"
            + $"&scope={Uri.EscapeDataString(SlackIntegrationConfig.BotScopes)}"
            + $"&redirect_uri={Uri.EscapeDataString(settings.SlackOAuthRedirectUri!)}"
            + $"&state={Uri.EscapeDataString(state)}";
        return Task.FromResult(ServiceResult<string>.Ok(url));
    }
}

// ── Complete install (OAuth callback) ────────────────────────────────────────

/// <param name="Error">Slack's own error, e.g. "access_denied" when the user cancels.</param>
public record CompleteSlackInstallCommand(string? Code, string? State, string? Error, string? IpAddress)
    : IRequest<ServiceResult<string>>;

/// <summary>
/// Runs on the anonymous callback Slack redirects the browser to, so no caller org is set and lookups span
/// all orgs; the signed state is what says which org this install is for. Returns the connected workspace's
/// name, or a failure whose code the controller turns into a reason on the redirect back to the app.
/// </summary>
public class CompleteSlackInstallHandler(ISlackOAuthClient oauth, ISlackInstallationRepository installations,
    ISecretProtector protector, IAppSettings settings, IAuditLogRepository audit)
    : IRequestHandler<CompleteSlackInstallCommand, ServiceResult<string>>
{
    public async Task<ServiceResult<string>> Handle(CompleteSlackInstallCommand cmd, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cmd.Error))
            return ServiceResult<string>.Fail("cancelled", "Slack install was cancelled.");
        if (!SlackIntegrationConfig.IsConfigured(settings, protector))
            return ServiceResult<string>.Fail("not_configured", "Slack isn't set up on this Pulse server.");
        if (!SlackInstallState.TryRead(cmd.State, DateTimeOffset.UtcNow, settings.JwtSecretKey, out var orgId, out var engineerId))
            return ServiceResult<string>.Fail("invalid_state", "This Slack install link is invalid or has expired. Start again from Pulse.");
        if (string.IsNullOrEmpty(cmd.Code))
            return ServiceResult<string>.Fail("invalid_request", "Slack didn't return an authorization code.");

        var result = await oauth.ExchangeCodeAsync(cmd.Code, ct);
        if (!result.Ok || result.TeamId is null || result.BotToken is null)
            return ServiceResult<string>.Fail("exchange_failed", "Slack didn't complete the install.");

        // A workspace's events carry only its team id, so it can belong to one organization only.
        var byTeam = await installations.GetByTeamIdAsync(result.TeamId, ct);
        if (byTeam is not null && byTeam.OrganizationId != orgId)
            return ServiceResult<string>.Fail("workspace_taken", "That Slack workspace is already connected to another organization.");

        var encrypted = protector.Protect(result.BotToken);
        var existing = await installations.GetByOrganizationAsync(orgId, ct);
        if (existing is not null && existing.TeamId == result.TeamId)
        {
            existing.Reinstall(result.TeamName ?? result.TeamId, result.BotUserId ?? "", encrypted, engineerId);
        }
        else
        {
            if (existing is not null)
                installations.Remove(existing); // switching to a different workspace
            await installations.AddAsync(SlackInstallation.Create(orgId, result.TeamId, result.TeamName ?? result.TeamId,
                result.BotUserId ?? "", encrypted, engineerId), ct);
        }
        await installations.SaveChangesAsync(ct);

        await audit.LogAsync("SLACK_CONNECTED", engineerId, cmd.IpAddress,
            $"Connected Slack workspace {result.TeamId} ({result.TeamName}) to organization {orgId}", ct);
        return ServiceResult<string>.Ok(result.TeamName ?? result.TeamId);
    }
}

// ── Disconnect ───────────────────────────────────────────────────────────────

public record DisconnectSlackCommand(Guid ActorId, string? IpAddress) : IRequest<ServiceResult<bool>>;

public class DisconnectSlackHandler(ICurrentUserService currentUser, ISlackInstallationRepository installations,
    IAuditLogRepository audit) : IRequestHandler<DisconnectSlackCommand, ServiceResult<bool>>
{
    public async Task<ServiceResult<bool>> Handle(DisconnectSlackCommand cmd, CancellationToken ct)
    {
        if (currentUser.OrganizationId is not Guid orgId || await installations.GetByOrganizationAsync(orgId, ct) is not { } installation)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Slack isn't connected.");

        installations.Remove(installation);
        await installations.SaveChangesAsync(ct);
        await audit.LogAsync("SLACK_DISCONNECTED", cmd.ActorId, cmd.IpAddress,
            $"Disconnected Slack workspace {installation.TeamId} from organization {orgId}", ct);
        return ServiceResult<bool>.Ok(true);
    }
}
