using System.Security.Claims;
using Pulse.Api.Security;
using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Pulse.UnitTests.Security;

public class HttpRlsContextTests
{
    private readonly Mock<IHttpContextAccessor> _accessor = new();

    private readonly BackgroundOrganizationContext _background = new();

    private HttpRlsContext CreateContext() =>
        new(_accessor.Object, new CurrentUserService(_accessor.Object, _background));

    [Fact]
    public void Resolve_returns_service_when_there_is_no_ambient_HttpContext()
    {
        _accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var (role, userId, orgId) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
        orgId.Should().Be("");
    }

    [Fact]
    public void Resolve_returns_the_authenticated_users_real_role_and_id()
    {
        var userId = Guid.NewGuid().ToString();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "head_of_pmo"), new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "Test");
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, resolvedId, _) = CreateContext().Resolve();

        role.Should().Be("head_of_pmo");
        resolvedId.Should().Be(userId);
    }

    [Fact]
    public void Resolve_returns_empty_role_for_an_unauthenticated_request()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, userId, orgId) = CreateContext().Resolve();

        role.Should().Be("");
        userId.Should().Be("");
        orgId.Should().Be("");
    }

    [Fact]
    public void Resolve_returns_service_when_RlsServiceOverride_is_applied_despite_no_authenticated_user()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        RlsServiceOverride.Apply(ctx);
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, userId, orgId) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
        orgId.Should().Be("");
    }

    [Fact]
    public void Resolve_returns_the_authenticated_users_organization()
    {
        var orgId = Guid.NewGuid();
        _accessor.Setup(a => a.HttpContext).Returns(AuthenticatedContext(
            new Claim(ClaimTypes.Role, "engineer"),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(PulseClaimTypes.OrganizationId, orgId.ToString())));

        var (_, _, resolvedOrg) = CreateContext().Resolve();

        resolvedOrg.Should().Be(orgId.ToString());
    }

    [Fact]
    public void Resolve_fails_closed_for_an_authenticated_token_without_an_organization()
    {
        var userId = Guid.NewGuid().ToString();
        _accessor.Setup(a => a.HttpContext).Returns(AuthenticatedContext(
            new Claim(ClaimTypes.Role, "engineer"),
            new Claim(ClaimTypes.NameIdentifier, userId)));

        var (role, resolvedId, orgId) = CreateContext().Resolve();

        role.Should().Be("engineer");
        resolvedId.Should().Be(userId);
        orgId.Should().Be(Guid.Empty.ToString(), "an authenticated caller with no org must match no organization, not all of them");
    }

    private static DefaultHttpContext AuthenticatedContext(params Claim[] claims) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")) };

    [Fact]
    public void The_service_override_keeps_an_authenticated_callers_organization()
    {
        var orgId = Guid.NewGuid();
        var ctx = AuthenticatedContext(
            new Claim(ClaimTypes.Role, "engineer"),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(PulseClaimTypes.OrganizationId, orgId.ToString()));
        RlsServiceOverride.Apply(ctx);
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, userId, resolvedOrg) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
        resolvedOrg.Should().Be(orgId.ToString(), "the override lifts project-level RLS, not the organization boundary");
    }

    [Fact]
    public void Background_work_run_for_an_organization_is_stamped_with_it()
    {
        var orgId = Guid.NewGuid();
        _background.OrganizationId = orgId;
        _accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var (role, userId, resolvedOrg) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
        resolvedOrg.Should().Be(orgId.ToString());
    }

    [Fact]
    public void A_service_token_resolves_to_the_org_agnostic_service_role()
    {
        _accessor.Setup(a => a.HttpContext).Returns(AuthenticatedContext(new Claim("serviceId", "scheduler")));

        var (role, userId, orgId) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
        orgId.Should().Be("");
    }
}
