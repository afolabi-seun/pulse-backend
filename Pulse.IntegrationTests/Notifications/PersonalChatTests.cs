using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pulse.IntegrationTests.Notifications;

/// <summary>Personal chat notifications: each person opts in to Slack or Google Chat direct messages.</summary>
[Collection("Integration")]
public class PersonalChatTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>, IDisposable
{
    private readonly WebApplicationFactory<Program> _app;

    public PersonalChatTests(PulseWebApplicationFactory factory) : base(factory)
    {
        // Google Chat's request signing is faked; everything behind the events endpoint is real.
        _app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleChatRequestVerifier>();
            services.AddSingleton<IGoogleChatRequestVerifier, AlwaysValidVerifier>();
        }));
    }

    public void Dispose() => _app.Dispose();

    private async Task<(string Email, HttpClient Client)> NewUserAsync()
    {
        var email = $"chat_{Guid.NewGuid():N}"[..18] + "@pulse.io";
        await SeedEngineerAsync(email);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginTokenAsync(email));
        return (email, client);
    }

    private static async Task<JsonElement> ChatSettingsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/chat", JsonOpts)).GetProperty("data");

    [Fact]
    public async Task Personal_chat_is_off_until_chosen()
    {
        var (_, client) = await NewUserAsync();

        var settings = await ChatSettingsAsync(client);

        settings.GetProperty("channel").GetString().Should().Be(ChatChannel.None);
        settings.GetProperty("googleChatLinked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Slack_cannot_be_chosen_before_the_organization_connects_it()
    {
        var (_, client) = await NewUserAsync();

        var response = await client.PutAsJsonAsync("/api/v1/notifications/chat", new { channel = ChatChannel.Slack });

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain("hasn't connected Slack");
    }

    [Fact]
    public async Task Google_Chat_needs_a_direct_message_with_Pulse_first_then_can_be_chosen()
    {
        var (email, client) = await NewUserAsync();

        (await client.PutAsJsonAsync("/api/v1/notifications/chat", new { channel = ChatChannel.GoogleChat }))
            .IsSuccessStatusCode.Should().BeFalse("no DM yet");

        var reply = await ChatEventAsync(new
        {
            type = "ADDED_TO_SPACE",
            space = new { name = $"spaces/DM{Guid.NewGuid():N}"[..20], type = "DM" },
            user = new { email },
        });
        reply.Should().Contain("now linked to your Pulse account");
        (await ChatSettingsAsync(client)).GetProperty("googleChatLinked").GetBoolean().Should().BeTrue();

        var chosen = await client.PutAsJsonAsync("/api/v1/notifications/chat", new { channel = ChatChannel.GoogleChat });
        chosen.StatusCode.Should().Be(HttpStatusCode.OK, await chosen.Content.ReadAsStringAsync());
        (await ChatSettingsAsync(client)).GetProperty("channel").GetString().Should().Be(ChatChannel.GoogleChat);
    }

    [Fact]
    public async Task A_direct_message_from_an_unknown_email_links_nothing()
    {
        var reply = await ChatEventAsync(new
        {
            type = "ADDED_TO_SPACE",
            space = new { name = "spaces/DMUNKNOWN", type = "DM" },
            user = new { email = "nobody@nowhere.test" },
        });

        reply.Should().Contain("couldn't find a Pulse account");
    }

    [Fact]
    public async Task Chat_can_be_switched_off_per_kind_independently_of_email()
    {
        var (_, client) = await NewUserAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/notifications/preferences/{NotificationKind.TaskAssigned}", new { chat = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var pref = (await client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/preferences", JsonOpts))
            .GetProperty("data").EnumerateArray().Single(p => p.GetProperty("kind").GetString() == NotificationKind.TaskAssigned);
        pref.GetProperty("chat").GetBoolean().Should().BeFalse();
        pref.GetProperty("email").GetBoolean().Should().BeTrue("only chat was changed");
    }

    private async Task<string> ChatEventAsync(object payload)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "faked-google-token");
        var response = await client.PostAsJsonAsync("/api/v1/integrations/google-chat/events", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
    }

    private sealed class AlwaysValidVerifier : IGoogleChatRequestVerifier
    {
        public Task<bool> VerifyAsync(string? bearerToken, CancellationToken ct = default) => Task.FromResult(bearerToken is not null);
    }
}
