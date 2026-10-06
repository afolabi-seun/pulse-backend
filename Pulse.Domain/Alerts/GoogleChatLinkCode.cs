using System.Security.Cryptography;
using System.Text;
using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>
/// A one-time code a head issues in Pulse and types into a Google Chat space ("@Pulse link ABCD-2345") to link
/// that space to their organization (multi-tenancy Phase 2c). Short-lived and single-use; only a hash is stored,
/// so a database read doesn't hand out live codes.
/// </summary>
public class GoogleChatLinkCode : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    // No 0/O, 1/I/L: the code is read off one screen and typed into another.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public Guid OrganizationId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public Guid CreatedByEngineerId { get; private set; }

    private GoogleChatLinkCode() { }

    /// <summary>Returns the entity to store and the code to show the head, formatted "ABCD-2345".</summary>
    public static (GoogleChatLinkCode LinkCode, string Code) Issue(Guid organizationId, Guid engineerId, DateTime now)
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 8);
        var code = $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
        return (new GoogleChatLinkCode
        {
            OrganizationId = organizationId,
            CodeHash = Hash(code),
            ExpiresAt = now.Add(Lifetime),
            CreatedByEngineerId = engineerId,
        }, code);
    }

    /// <summary>Normalizes what someone typed (case, spaces, missing hyphen) before hashing.</summary>
    public static string Hash(string code)
    {
        var normalized = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length == 8)
            normalized = $"{normalized[..4]}-{normalized[4..]}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;
}
