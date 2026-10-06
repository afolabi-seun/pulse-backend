using System.Security.Cryptography;
using System.Text;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.Integrations;

/// <summary>
/// AES-256-GCM with the key from INTEGRATION_ENCRYPTION_KEY. Output is <c>v1:</c> + base64(nonce ‖ tag ‖
/// ciphertext); the version prefix leaves room to rotate to a new key or scheme later. GCM authenticates the
/// ciphertext, so a tampered or wrong-key value fails to decrypt instead of returning garbage.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[]? _key;

    public AesGcmSecretProtector(IAppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.IntegrationEncryptionKey))
            return;
        var key = Convert.FromBase64String(settings.IntegrationEncryptionKey);
        if (key.Length != 32)
            throw new InvalidOperationException("INTEGRATION_ENCRYPTION_KEY must be base64 of exactly 32 bytes (openssl rand -base64 32).");
        _key = key;
    }

    public bool IsConfigured => _key is not null;

    public string Protect(string plaintext)
    {
        var key = RequireKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        return Version + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue)
    {
        var key = RequireKey();
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
            throw new CryptographicException("Unrecognised protected value format.");
        var bytes = Convert.FromBase64String(protectedValue[Version.Length..]);
        if (bytes.Length < NonceSize + TagSize)
            throw new CryptographicException("Protected value is too short.");
        var nonce = bytes.AsSpan(0, NonceSize);
        var tag = bytes.AsSpan(NonceSize, TagSize);
        var cipher = bytes.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] RequireKey() =>
        _key ?? throw new InvalidOperationException("INTEGRATION_ENCRYPTION_KEY is not configured.");
}
