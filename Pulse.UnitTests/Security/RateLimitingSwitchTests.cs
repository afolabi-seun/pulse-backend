using Pulse.Api.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Pulse.UnitTests.Security;

/// <summary>The rate limiter can be switched off for local end-to-end runs, but never anywhere but Development.</summary>
public class RateLimitingSwitchTests
{
    private static RateLimiterOptions Resolve(string environment, string? disableRateLimiting)
    {
        var settings = new Dictionary<string, string?>();
        if (disableRateLimiting is not null) settings["DISABLE_RATE_LIMITING"] = disableRateLimiting;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environment);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(env.Object);
        services.AddPulseRateLimiting();

        return services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;
    }

    [Fact]
    public void The_limiter_is_on_by_default() =>
        Resolve(Environments.Development, null).GlobalLimiter.Should().NotBeNull();

    [Fact]
    public void Development_with_the_switch_set_turns_it_off() =>
        Resolve(Environments.Development, "true").GlobalLimiter.Should().BeNull();

    [Fact]
    public void Development_with_the_switch_false_leaves_it_on() =>
        Resolve(Environments.Development, "false").GlobalLimiter.Should().NotBeNull();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_Development_the_switch_is_ignored(string environment) =>
        Resolve(environment, "true").GlobalLimiter.Should().NotBeNull();
}
