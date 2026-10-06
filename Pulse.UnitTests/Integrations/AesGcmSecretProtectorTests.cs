using System.Security.Cryptography;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Integrations;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Integrations;

public class AesGcmSecretProtectorTests
{
    private static AesGcmSecretProtector Create(string? key) =>
        new(Mock.Of<IAppSettings>(s => s.IntegrationEncryptionKey == key));

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Protected_values_round_trip_and_never_contain_the_plaintext()
    {
        var protector = Create(NewKey());

        var protectedValue = protector.Protect("xoxb-secret-token");

        protectedValue.Should().StartWith("v1:").And.NotContain("xoxb");
        protector.Unprotect(protectedValue).Should().Be("xoxb-secret-token");
    }

    [Fact]
    public void The_same_plaintext_encrypts_differently_each_time()
    {
        var protector = Create(NewKey());

        protector.Protect("same").Should().NotBe(protector.Protect("same"));
    }

    [Fact]
    public void A_value_protected_with_another_key_fails_to_decrypt()
    {
        var protectedValue = Create(NewKey()).Protect("xoxb-secret-token");

        var act = () => Create(NewKey()).Unprotect(protectedValue);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_tampered_value_fails_to_decrypt()
    {
        var protector = Create(NewKey());
        var bytes = Convert.FromBase64String(protector.Protect("xoxb-secret-token")[3..]);
        bytes[^1] ^= 0xFF;

        var act = () => protector.Unprotect("v1:" + Convert.ToBase64String(bytes));

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Without_a_key_it_reports_unconfigured_and_refuses_to_protect()
    {
        var protector = Create(null);

        protector.IsConfigured.Should().BeFalse();
        protector.Invoking(p => p.Protect("x")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_key_of_the_wrong_length_is_rejected_at_startup()
    {
        var act = () => Create(Convert.ToBase64String(new byte[16]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }
}
