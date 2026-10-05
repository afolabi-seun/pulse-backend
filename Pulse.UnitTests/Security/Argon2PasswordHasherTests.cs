using Pulse.Infrastructure.Security;
using FluentAssertions;

namespace Pulse.UnitTests.Security;

/// <summary>
/// The real Argon2id hasher. The integration tests swap in a fast hasher (Argon2 at 64 MB and 4 passes costs a few hundred
/// milliseconds a call, and those tests hash and verify constantly), so this is where the real one is exercised.
/// </summary>
public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void A_hash_verifies_against_the_password_it_came_from()
    {
        var hash = _hasher.Hash("Str0ng!Pass12");

        _hasher.Verify("Str0ng!Pass12", hash).Should().BeTrue();
    }

    [Fact]
    public void A_different_password_does_not_verify()
    {
        var hash = _hasher.Hash("Str0ng!Pass12");

        _hasher.Verify("Str0ng!Pass13", hash).Should().BeFalse();
    }

    [Fact]
    public void Hashing_the_same_password_twice_gives_different_hashes_because_the_salt_is_random()
    {
        var first = _hasher.Hash("Str0ng!Pass12");
        var second = _hasher.Hash("Str0ng!Pass12");

        first.Should().NotBe(second);
        _hasher.Verify("Str0ng!Pass12", first).Should().BeTrue();
        _hasher.Verify("Str0ng!Pass12", second).Should().BeTrue();
    }

    [Fact]
    public void The_hash_does_not_contain_the_password()
    {
        _hasher.Hash("Str0ng!Pass12").Should().NotContain("Str0ng!Pass12");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$dummy$dummy")]   // the placeholder login verifies against for an unknown email
    public void A_malformed_stored_hash_never_verifies(string stored)
    {
        _hasher.Verify("Str0ng!Pass12", stored).Should().BeFalse();
    }
}
