using Pulse.Domain.Common;

namespace Pulse.Domain.Integrations;

/// <summary>
/// An organization's Slack workspace connection (multi-tenancy Phase 2b): the result of its "Add to Slack"
/// OAuth install. One per organization, and one organization per Slack workspace — a workspace's events
/// carry only its team id, so it must map to exactly one org. The bot token is stored encrypted
/// (ISecretProtector); only Infrastructure ever decrypts it.
/// </summary>
public class SlackInstallation : Entity
{
    public Guid OrganizationId { get; private set; }
    /// <summary>Slack's workspace id (e.g. T0123ABCD) — how incoming events find their organization.</summary>
    public string TeamId { get; private set; } = string.Empty;
    public string TeamName { get; private set; } = string.Empty;
    public string BotUserId { get; private set; } = string.Empty;
    public string EncryptedBotToken { get; private set; } = string.Empty;
    public Guid InstalledByEngineerId { get; private set; }

    private SlackInstallation() { }

    public static SlackInstallation Create(Guid organizationId, string teamId, string teamName, string botUserId,
        string encryptedBotToken, Guid installedByEngineerId) => new()
    {
        OrganizationId = organizationId,
        TeamId = teamId,
        TeamName = teamName,
        BotUserId = botUserId,
        EncryptedBotToken = encryptedBotToken,
        InstalledByEngineerId = installedByEngineerId,
    };

    /// <summary>A reinstall (same org, same workspace) — Slack issues a new token.</summary>
    public void Reinstall(string teamName, string botUserId, string encryptedBotToken, Guid installedByEngineerId)
    {
        TeamName = teamName;
        BotUserId = botUserId;
        EncryptedBotToken = encryptedBotToken;
        InstalledByEngineerId = installedByEngineerId;
        CreatedAt = DateTime.UtcNow;
    }
}
