using Pulse.Domain.Alerts;
using FluentAssertions;

namespace Pulse.UnitTests.Alerts;

public class GoogleChatLinkCodeTests
{
    [Fact]
    public void An_issued_code_is_two_groups_of_four_unambiguous_characters_and_only_its_hash_is_kept()
    {
        var (linkCode, code) = GoogleChatLinkCode.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        code.Should().MatchRegex("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$");
        linkCode.CodeHash.Should().Be(GoogleChatLinkCode.Hash(code)).And.NotContain(code.Replace("-", ""));
    }

    [Theory]
    [InlineData("abcd-2345")]
    [InlineData("ABCD2345")]
    [InlineData(" abcd 2345 ")]
    public void Typing_variations_of_a_code_hash_the_same(string typed)
    {
        GoogleChatLinkCode.Hash(typed).Should().Be(GoogleChatLinkCode.Hash("ABCD-2345"));
    }

    [Fact]
    public void A_code_expires_after_its_lifetime()
    {
        var now = DateTime.UtcNow;
        var (linkCode, _) = GoogleChatLinkCode.Issue(Guid.NewGuid(), Guid.NewGuid(), now);

        linkCode.IsExpired(now.Add(GoogleChatLinkCode.Lifetime).AddSeconds(-1)).Should().BeFalse();
        linkCode.IsExpired(now.Add(GoogleChatLinkCode.Lifetime)).Should().BeTrue();
    }

    [Fact]
    public void Codes_are_random()
    {
        var codes = Enumerable.Range(0, 50).Select(_ => GoogleChatLinkCode.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow).Code);

        codes.Should().OnlyHaveUniqueItems();
    }
}
