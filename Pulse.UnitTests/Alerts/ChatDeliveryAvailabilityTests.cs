using Pulse.Application.Alerts;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Integrations;
using Pulse.Domain.Organizations;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class ChatDeliveryAvailabilityTests
{
    private readonly Mock<ISlackInstallationRepository> _slack = new();
    // Stands in for the org-filtered space lookup: returns a space only if it's visible to the caller's org.
    private readonly Mock<IGoogleChatSpaceRepository> _spaces = new();

    private static ICurrentUserService In(Guid? organizationId) =>
        Mock.Of<ICurrentUserService>(u => u.OrganizationId == organizationId);

    private Task<string?> Check(Guid? orgId, string? slackChannel, string? spaceId) =>
        ChatDeliveryAvailability.CheckAsync(In(orgId), _slack.Object, _spaces.Object, slackChannel, spaceId);

    private Guid ConnectedOrganization()
    {
        var orgId = Guid.NewGuid();
        _slack.Setup(s => s.GetByOrganizationAsync(orgId, default))
            .ReturnsAsync(SlackInstallation.Create(orgId, "T1", "Acme", "U1", "v1:x", Guid.NewGuid()));
        return orgId;
    }

    private void LinkedSpace(string spaceId, Guid orgId)
    {
        var space = GoogleChatSpace.Create(spaceId, "Alerts");
        space.LinkTo(orgId);
        _spaces.Setup(s => s.GetBySpaceIdAsync(spaceId, default)).ReturnsAsync(space);
    }

    [Fact]
    public async Task An_organization_without_a_Slack_connection_cannot_route_alerts_to_Slack()
    {
        (await Check(Guid.NewGuid(), "#alerts", null)).Should().Be(ChatDeliveryAvailability.SlackUnavailableMessage);
    }

    [Fact]
    public async Task An_organization_that_connected_Slack_can_route_alerts_to_it()
    {
        (await Check(ConnectedOrganization(), "#alerts", null)).Should().BeNull();
    }

    [Fact]
    public async Task A_Google_Chat_space_linked_to_the_organization_can_be_used()
    {
        var orgId = Guid.NewGuid();
        LinkedSpace("spaces/MINE", orgId);

        (await Check(orgId, null, "spaces/MINE")).Should().BeNull();
    }

    [Fact]
    public async Task A_Google_Chat_space_not_visible_to_the_organization_cannot_be_used()
    {
        // Another org's space, or a made-up id: the org-filtered lookup finds nothing.
        (await Check(Guid.NewGuid(), null, "spaces/THEIRS")).Should().Be(ChatDeliveryAvailability.GoogleChatUnavailableMessage);
    }

    [Fact]
    public async Task An_unlinked_Google_Chat_space_cannot_be_used()
    {
        _spaces.Setup(s => s.GetBySpaceIdAsync("spaces/NEW", default)).ReturnsAsync(GoogleChatSpace.Create("spaces/NEW", "New"));

        (await Check(Organization.DefaultId, null, "spaces/NEW")).Should().Be(ChatDeliveryAvailability.GoogleChatUnavailableMessage);
    }

    [Fact]
    public async Task Any_organization_can_create_rules_without_chat_delivery()
    {
        (await Check(Guid.NewGuid(), null, " ")).Should().BeNull();
    }

    [Fact]
    public async Task The_default_organization_keeps_its_legacy_Slack_and_its_linked_spaces()
    {
        LinkedSpace("spaces/LEGACY", Organization.DefaultId);

        (await Check(Organization.DefaultId, "#alerts", "spaces/LEGACY")).Should().BeNull();
        (await Check(null, "#alerts", null)).Should().BeNull("background work runs as the default org");
    }
}
