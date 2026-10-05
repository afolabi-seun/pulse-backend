using System.Security.Claims;
using Pulse.Api.Security;
using Pulse.Application.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Pulse.UnitTests.Security;

public class CurrentUserServiceTests
{
    private readonly Mock<IHttpContextAccessor> _accessor = new();

    private CurrentUserService Create(HttpContext? ctx)
    {
        _accessor.Setup(a => a.HttpContext).Returns(ctx);
        return new CurrentUserService(_accessor.Object);
    }

    [Fact]
    public void Reads_user_id_role_and_organization_from_the_claims()
    {
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var user = Create(Authenticated(
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "team_lead"),
            new Claim(PulseClaimTypes.OrganizationId, orgId.ToString())));

        user.IsAuthenticated.Should().BeTrue();
        user.UserId.Should().Be(userId);
        user.Role.Should().Be("team_lead");
        user.OrganizationId.Should().Be(orgId);
    }

    [Fact]
    public void Everything_is_null_without_an_HttpContext()
    {
        var user = Create(null);

        user.IsAuthenticated.Should().BeFalse();
        user.UserId.Should().BeNull();
        user.Role.Should().BeNull();
        user.OrganizationId.Should().BeNull();
    }

    [Fact]
    public void Claims_on_an_unauthenticated_identity_are_ignored()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(PulseClaimTypes.OrganizationId, Guid.NewGuid().ToString()),
        }); // no authenticationType → not authenticated
        var user = Create(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });

        user.IsAuthenticated.Should().BeFalse();
        user.UserId.Should().BeNull();
        user.OrganizationId.Should().BeNull();
    }

    [Fact]
    public void A_missing_or_malformed_organization_claim_is_null()
    {
        Create(Authenticated(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())))
            .OrganizationId.Should().BeNull();

        Create(Authenticated(new Claim(PulseClaimTypes.OrganizationId, "not-a-guid")))
            .OrganizationId.Should().BeNull();
    }

    private static DefaultHttpContext Authenticated(params Claim[] claims) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")) };
}
