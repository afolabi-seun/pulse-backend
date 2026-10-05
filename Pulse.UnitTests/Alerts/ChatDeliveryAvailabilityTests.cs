using Pulse.Application.Alerts;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class ChatDeliveryAvailabilityTests
{
    private static ICurrentUserService In(Guid? organizationId) =>
        Mock.Of<ICurrentUserService>(u => u.OrganizationId == organizationId);

    [Theory]
    [InlineData("#alerts", null)]
    [InlineData(null, "spaces/AAA")]
    public void Another_organization_cannot_use_Slack_or_Google_Chat_yet(string? slack, string? space)
    {
        ChatDeliveryAvailability.Check(In(Guid.NewGuid()), slack, space)
            .Should().Be(ChatDeliveryAvailability.UnavailableMessage);
    }

    [Fact]
    public void Another_organization_can_still_create_rules_without_chat_delivery()
    {
        ChatDeliveryAvailability.Check(In(Guid.NewGuid()), null, " ").Should().BeNull();
    }

    [Fact]
    public void The_default_organization_keeps_Slack_and_Google_Chat()
    {
        ChatDeliveryAvailability.Check(In(Organization.DefaultId), "#alerts", "spaces/AAA").Should().BeNull();
        ChatDeliveryAvailability.Check(In(null), "#alerts", null).Should().BeNull("background work runs as the default org");
    }
}
