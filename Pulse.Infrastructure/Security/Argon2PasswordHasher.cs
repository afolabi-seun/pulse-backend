using Pulse.Application.Common.Interfaces;
using Konscious.Security.Cryptography;
using System.Security.Cryptography;
using System.Text;

namespace Pulse.Infrastructure.Security;

public class Argon2PasswordHasher : IPasswordHasher
{
    // memory: 64MB, iterations: 4, parallelism: 2 — tuned for ≥250ms on target hardware
    private const int MemorySize = 65536;
    private const int Iterations = 4;
    private const int DegreeOfParallelism = 2;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = ComputeHash(Encoding.UTF8.GetBytes(password), salt);

        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;

        var salt = Convert.FromBase64String(parts[0]);
        var expectedHash = Convert.FromBase64String(parts[1]);
        var actualHash = ComputeHash(Encoding.UTF8.GetBytes(password), salt);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static byte[] ComputeHash(byte[] password, byte[] salt)
    {
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = DegreeOfParallelism
        };
        return argon2.GetBytes(HashLength);
    }
}
