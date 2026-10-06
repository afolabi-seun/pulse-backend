using Pulse.Application.Integrations.Slack;
using FluentAssertions;

namespace Pulse.UnitTests.Integrations;

public class SlackInstallStateTests
{
    private const string Key = "test-signing-key-minimum-32-chars-long!!";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void A_state_round_trips_its_organization_and_installer()
    {
        var org = Guid.NewGuid();
        var engineer = Guid.NewGuid();

        var state = SlackInstallState.Create(org, engineer, Now, Key);

        SlackInstallState.TryRead(state, Now.AddMinutes(5), Key, out var readOrg, out var readEngineer).Should().BeTrue();
        readOrg.Should().Be(org);
        readEngineer.Should().Be(engineer);
    }

    [Fact]
    public void An_expired_state_is_rejected()
    {
        var state = SlackInstallState.Create(Guid.NewGuid(), Guid.NewGuid(), Now, Key);

        SlackInstallState.TryRead(state, Now.Add(SlackInstallState.Lifetime).AddSeconds(1), Key, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_state_signed_with_another_key_is_rejected()
    {
        var state = SlackInstallState.Create(Guid.NewGuid(), Guid.NewGuid(), Now, "another-signing-key-also-32-chars-long!");

        SlackInstallState.TryRead(state, Now, Key, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_state_whose_organization_was_swapped_is_rejected()
    {
        // Re-encode the payload for a different org but keep the original signature: forging an install
        // into someone else's organization must fail.
        var state = SlackInstallState.Create(Guid.NewGuid(), Guid.NewGuid(), Now, Key);
        var forgedPayload = SlackInstallState.Create(Guid.NewGuid(), Guid.NewGuid(), Now, Key).Split('.')[0];
        var forged = $"{forgedPayload}.{state.Split('.')[1]}";

        SlackInstallState.TryRead(forged, Now, Key, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-state")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Malformed_states_are_rejected(string? state)
    {
        SlackInstallState.TryRead(state, Now, Key, out _, out _).Should().BeFalse();
    }
}
