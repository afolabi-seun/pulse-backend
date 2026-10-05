using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Bootstrap;

/// <summary>
/// Happy-path bootstrap test — isolated in its own class so it always gets a fresh empty DB,
/// unaffected by the seeding that happens in the error-case tests below.
/// </summary>
[Collection("Bootstrap")]
public class BootstrapFirstCallTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public BootstrapFirstCallTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Bootstrap_creates_first_head_and_returns_201()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
        {
            name = "First Head",
            email = "head@pulse.io",
            password = "Str0ng!Pass12"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        body!.Status.Should().Be("success");
        body.Data!.AccessToken.Should().NotBeNullOrEmpty();
        body.Data.RefreshToken.Should().NotBeNullOrEmpty();
    }
}

/// <summary>
/// Error-case bootstrap tests — share a factory; the first test seeds an engineer
/// to simulate an already-initialised instance.
/// </summary>
[Collection("Bootstrap")]
public class BootstrapErrorCaseTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public BootstrapErrorCaseTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Bootstrap_returns_409_on_second_call()
    {
        await SeedEngineerAsync("existing@pulse.io");

        var response = await Client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
        {
            name = "Another Head",
            email = "another@pulse.io",
            password = "Str0ng!Pass12"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Bootstrap_returns_400_for_short_password()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
        {
            name = "Head",
            email = "head2@pulse.io",
            password = "short"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bootstrap_returns_400_when_email_is_missing()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/bootstrap", new
        {
            name = "Head",
            password = "Str0ng!Pass12"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
