using Pulse.Domain.Organizations;

namespace Pulse.Infrastructure.Persistence;

public class ThresholdSetting
{
    /// <summary>Settings are per organization: (OrganizationId, Key) is the key.</summary>
    public Guid OrganizationId { get; set; } = Organization.DefaultId;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
