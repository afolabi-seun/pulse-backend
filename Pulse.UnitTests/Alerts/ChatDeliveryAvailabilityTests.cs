using Pulse.Application.Alerts;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Integrations;
using Pulse.Domain.Organizations;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class ChatDeliveryAvailabilityTests
{
    private readonly Mock<ISlackInstallationRepository> _slack = new();

    private static ICurrentUserService In(Guid? organizationId) =>
        Mock.Of<ICurrentUserService>(u => u.OrganizationId == organizationId);

    private Guid ConnectedOrganization()
    {
        var orgId = Guid.NewGuid();
        _slack.Setup(s => s.GetByOrganizationAsync(orgId, default))
            .ReturnsAsync(SlackInstallation.Create(orgId, "T1", "Acme", "U1", "v1:x", Guid.NewGuid()));
        return orgId;
    }

    [Fact]
    public async Task An_organization_without_a_Slack_connection_cannot_route_alerts_to_Slack()
    {
        (await ChatDeliveryAvailability.CheckAsync(In(Guid.NewGuid()), _slack.Object, "#alerts", null))
            .Should().Be(ChatDeliveryAvailability.SlackUnavailableMessage);
    }

    [Fact]
    public async Task An_organization_that_connected_Slack_can_route_alerts_to_it()
    {
        (await ChatDeliveryAvailability.CheckAsync(In(ConnectedOrganization()), _slack.Object, "#alerts", null))
            .Should().BeNull();
    }

    [Fact]
    public async Task Another_organization_cannot_use_Google_Chat_yet_even_with_Slack_connected()
    {
        (await ChatDeliveryAvailability.CheckAsync(In(ConnectedOrganization()), _slack.Object, null, "spaces/AAA"))
            .Should().Be(ChatDeliveryAvailability.GoogleChatUnavailableMessage);
    }

    [Fact]
    public async Task Any_organization_can_create_rules_without_chat_delivery()
    {
        (await ChatDeliveryAvailability.CheckAsync(In(Guid.NewGuid()), _slack.Object, null, " ")).Should().BeNull();
    }

    [Fact]
    public async Task The_default_organization_keeps_Slack_and_Google_Chat()
    {
        (await ChatDeliveryAvailability.CheckAsync(In(Organization.DefaultId), _slack.Object, "#alerts", "spaces/AAA")).Should().BeNull();
        (await ChatDeliveryAvailability.CheckAsync(In(null), _slack.Object, "#alerts", null)).Should().BeNull("background work runs as the default org");
    }
}
