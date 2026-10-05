using System.Security.Cryptography;
using System.Text;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.Security;

/// <summary>
/// Checks passwords against the Have I Been Pwned Pwned Passwords API using k-anonymity:
/// only the first 5 hex chars of the SHA-1 hash are sent, preventing the full password or
/// hash from ever leaving this process.
/// </summary>
public class HibpBreachedPasswordChecker : IBreachedPasswordChecker
{
    private readonly HttpClient _httpClient;

    public HibpBreachedPasswordChecker(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<bool> IsBreachedAsync(string password, CancellationToken ct = default)
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var prefix = sha1[..5];
        var suffix = sha1[5..];

        var response = await _httpClient.GetStringAsync($"https://api.pwnedpasswords.com/range/{prefix}", ct);

        return response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line =>
            {
                var parts = line.Split(':', 2);
                return parts.Length == 2
                    && parts[0].Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(parts[1].Trim(), out var count)
                    && count > 0;
            });
    }
}
