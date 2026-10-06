using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Integrations;

public class SlackTokenProvider : ISlackTokenProvider
{
    private readonly ICurrentUserService _currentUser;
    private readonly ISlackInstallationRepository _installations;
    private readonly ISecretProtector _protector;
    private readonly IAppSettings _settings;
    private readonly ILogger<SlackTokenProvider> _logger;

    public SlackTokenProvider(ICurrentUserService currentUser, ISlackInstallationRepository installations,
        ISecretProtector protector, IAppSettings settings, ILogger<SlackTokenProvider> logger)
    {
        _currentUser = currentUser;
        _installations = installations;
        _protector = protector;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string?> GetBotTokenAsync(CancellationToken ct = default)
    {
        var orgId = _currentUser.OrganizationId ?? Organization.DefaultId;

        if (_protector.IsConfigured && await _installations.GetByOrganizationAsync(orgId, ct) is { } installation)
        {
            try
            {
                return _protector.Unprotect(installation.EncryptedBotToken);
            }
            catch (Exception ex)
            {
                // E.g. INTEGRATION_ENCRYPTION_KEY was changed: the org has to reconnect. Never fall back to
                // another org's token.
                _logger.LogError(ex, "Could not decrypt the Slack token for organization {OrganizationId}.", orgId);
                return null;
            }
        }

        // Before organizations connected their own workspaces, SLACK_BOT_TOKEN was the deployment's one
        // workspace — the default organization's. It stays that org's, and only that org's.
        return orgId == Organization.DefaultId && !string.IsNullOrEmpty(_settings.SlackBotToken)
            ? _settings.SlackBotToken
            : null;
    }
}
