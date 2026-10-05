using System.Security.Claims;
using Pulse.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Pulse.UnitTests.Security;

public class HttpRlsContextTests
{
    private readonly Mock<IHttpContextAccessor> _accessor = new();

    private HttpRlsContext CreateContext() => new(_accessor.Object);

    [Fact]
    public void Resolve_returns_service_when_there_is_no_ambient_HttpContext()
    {
        _accessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var (role, userId) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
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

        var (role, resolvedId) = CreateContext().Resolve();

        role.Should().Be("head_of_pmo");
        resolvedId.Should().Be(userId);
    }

    [Fact]
    public void Resolve_returns_empty_role_for_an_unauthenticated_request()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, userId) = CreateContext().Resolve();

        role.Should().Be("");
        userId.Should().Be("");
    }

    [Fact]
    public void Resolve_returns_service_when_RlsServiceOverride_is_applied_despite_no_authenticated_user()
    {
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        RlsServiceOverride.Apply(ctx);
        _accessor.Setup(a => a.HttpContext).Returns(ctx);

        var (role, userId) = CreateContext().Resolve();

        role.Should().Be("service");
        userId.Should().Be("");
    }
}
