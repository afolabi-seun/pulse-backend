using Pulse.Domain.Common;

namespace Pulse.Domain.Organizations;

/// <summary>
/// A customer organization — the outermost tenancy scope (see docs/design/multi-tenancy-and-billing.md).
/// Team, Engineer and Project carry an OrganizationId; everything else inherits it through them.
///
/// Phase 0: the table exists and every row belongs to the seeded default organization, but nothing
/// reads OrganizationId yet. PlanId is deliberately absent until Phase 3 introduces Plan.
/// </summary>
public class Organization : Entity
{
    /// <summary>The organization every pre-multi-tenancy row is assigned to. Fixed so the migration,
    /// the column default and the C# side all agree without a lookup.</summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000001");
    public const string DefaultSlug = "default";

    public string Name { get; private set; } = string.Empty;
    /// <summary>Unique, lowercase, URL-safe key — the future login/routing handle for an org.</summary>
    public string Slug { get; private set; } = string.Empty;
    public string? BillingEmail { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Organization() { }

    public static Organization Create(string name, string slug, string? billingEmail = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Organization name is required.", nameof(name));
        if (!IsValidSlug(slug))
            throw new ArgumentException("Slug must be 2–50 lowercase letters, digits or hyphens, not starting or ending with a hyphen.", nameof(slug));

        return new()
        {
            Name = name.Trim(),
            Slug = slug,
            BillingEmail = string.IsNullOrWhiteSpace(billingEmail) ? null : billingEmail.Trim(),
        };
    }

    public static bool IsValidSlug(string? slug) =>
        slug is { Length: >= 2 and <= 50 }
        && slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && slug[0] != '-' && slug[^1] != '-';

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Organization name is required.", nameof(name));
        Name = name.Trim();
    }

    public void SetBillingEmail(string? billingEmail) =>
        BillingEmail = string.IsNullOrWhiteSpace(billingEmail) ? null : billingEmail.Trim();

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
