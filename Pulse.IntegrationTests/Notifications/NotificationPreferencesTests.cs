using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Domain.Notifications;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Notifications;

/// <summary>Per-person notification preferences: whether each kind is emailed (the notification dispatcher).</summary>
[Collection("Integration")]
public class NotificationPreferencesTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public NotificationPreferencesTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<HttpClient> NewUserAsync()
    {
        var email = $"prefs_{Guid.NewGuid():N}"[..20] + "@pulse.io";
        await SeedEngineerAsync(email);
        return await AuthenticatedClientAsync(email);
    }

    private static async Task<Dictionary<string, JsonElement>> PreferencesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/notifications/preferences", JsonOpts))
            .GetProperty("data").EnumerateArray()
            .ToDictionary(p => p.GetProperty("kind").GetString()!);

    [Fact]
    public async Task Every_kind_is_listed_with_email_on_by_default()
    {
        var prefs = await PreferencesAsync(await NewUserAsync());

        prefs.Should().ContainKey(NotificationKind.TaskAssigned);
        prefs.Values.Should().OnlyContain(p => p.GetProperty("email").GetBoolean());
        prefs[NotificationKind.TaskAssigned].GetProperty("category").GetString().Should().Be("Tasks");
        prefs[NotificationKind.AccountLocked].GetProperty("emailLocked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Switching_a_kind_off_persists_and_only_for_that_person()
    {
        var client = await NewUserAsync();
        var other = await NewUserAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/notifications/preferences/{NotificationKind.TaskAssigned}", new { email = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await PreferencesAsync(client))[NotificationKind.TaskAssigned].GetProperty("email").GetBoolean().Should().BeFalse();
        (await PreferencesAsync(other))[NotificationKind.TaskAssigned].GetProperty("email").GetBoolean().Should().BeTrue();

        await client.PutAsJsonAsync($"/api/v1/notifications/preferences/{NotificationKind.TaskAssigned}", new { email = true });
        (await PreferencesAsync(client))[NotificationKind.TaskAssigned].GetProperty("email").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Security_notices_cannot_be_switched_off()
    {
        var client = await NewUserAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/notifications/preferences/{NotificationKind.PasswordReset}", new { email = false });

        response.IsSuccessStatusCode.Should().BeFalse();
        (await PreferencesAsync(client))[NotificationKind.PasswordReset].GetProperty("email").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_kind_is_not_found()
    {
        var response = await (await NewUserAsync()).PutAsJsonAsync("/api/v1/notifications/preferences/not_a_kind", new { email = false });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
