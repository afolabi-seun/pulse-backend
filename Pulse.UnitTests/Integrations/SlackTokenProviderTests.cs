using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Integrations;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Pulse.UnitTests.Integrations;

public class SlackTokenProviderTests
{
    private readonly Mock<ISlackInstallationRepository> _installations = new();
    private readonly Mock<ISecretProtector> _protector = new();

    public SlackTokenProviderTests()
    {
        _protector.SetupGet(p => p.IsConfigured).Returns(true);
        _protector.Setup(p => p.Unprotect(It.IsAny<string>())).Returns<string>(v => v.Replace("enc:", ""));
    }

    private SlackTokenProvider Create(Guid? organizationId, string? legacyToken = "xoxb-legacy") =>
        new(Mock.Of<ICurrentUserService>(u => u.OrganizationId == organizationId), _installations.Object, _protector.Object,
            Mock.Of<IAppSettings>(s => s.SlackBotToken == legacyToken), NullLogger<SlackTokenProvider>.Instance);

    private void Connect(Guid orgId, string token) =>
        _installations.Setup(i => i.GetByOrganizationAsync(orgId, default))
            .ReturnsAsync(SlackInstallation.Create(orgId, "T1", "Acme", "U1", $"enc:{token}", Guid.NewGuid()));

    [Fact]
    public async Task An_organization_posts_with_its_own_workspace_token()
    {
        var orgId = Guid.NewGuid();
        Connect(orgId, "xoxb-acme");

        (await Create(orgId).GetBotTokenAsync()).Should().Be("xoxb-acme");
    }

    [Fact]
    public async Task An_organization_without_a_connection_gets_no_token_never_the_legacy_one()
    {
        (await Create(Guid.NewGuid()).GetBotTokenAsync()).Should().BeNull();
    }

    [Fact]
    public async Task The_default_organization_falls_back_to_the_legacy_token()
    {
        (await Create(Organization.DefaultId).GetBotTokenAsync()).Should().Be("xoxb-legacy");
        (await Create(null).GetBotTokenAsync()).Should().Be("xoxb-legacy", "unscoped work is the default org's");
    }

    [Fact]
    public async Task The_default_organizations_own_connection_wins_over_the_legacy_token()
    {
        Connect(Organization.DefaultId, "xoxb-default-connected");

        (await Create(Organization.DefaultId).GetBotTokenAsync()).Should().Be("xoxb-default-connected");
    }

    [Fact]
    public async Task A_token_that_cannot_be_decrypted_yields_no_token_rather_than_a_fallback()
    {
        var orgId = Guid.NewGuid();
        Connect(orgId, "xoxb-acme");
        _protector.Setup(p => p.Unprotect(It.IsAny<string>())).Throws(new System.Security.Cryptography.CryptographicException());

        (await Create(orgId).GetBotTokenAsync()).Should().BeNull();
    }
}
