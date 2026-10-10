using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>Organization branding: each organization's own display name, accent colour and logo.</summary>
[Collection("Integration")]
public class OrganizationBrandingTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public OrganizationBrandingTests(PulseWebApplicationFactory factory) : base(factory) { }

    /// <summary>A new organization with one user of the given role, signed in.</summary>
    private async Task<HttpClient> UserInNewOrganizationAsync(string role = Roles.HeadOfRnD)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = await SeedEngineerAsync($"brand_{suffix}@pulse.io", role);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Organization.Create($"Brand org {suffix}", $"brand-{suffix}");
            db.Organizations.Add(org);
            var engineer = await db.Engineers.SingleAsync(e => e.Id == user.Id);
            db.Entry(engineer).Property(e => e.OrganizationId).CurrentValue = org.Id;
            await db.SaveChangesAsync();
        }
        return await AuthenticatedClientAsync(user.Email);
    }

    private static async Task<JsonElement> OrganizationAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/organization", JsonOpts)).GetProperty("data");

    private static MultipartFormDataContent LogoUpload(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", "logo.png" } };
    }

    [Fact]
    public async Task A_head_sets_the_organizations_name_and_colour()
    {
        var head = await UserInNewOrganizationAsync();

        var response = await head.PutAsJsonAsync("/api/v1/organization/branding", new { name = "Acme Engineering", brandColor = "#b8893b" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var org = await OrganizationAsync(head);
        org.GetProperty("name").GetString().Should().Be("Acme Engineering");
        org.GetProperty("brandColor").GetString().Should().Be("#B8893B");
    }

    [Fact]
    public async Task Only_a_head_can_change_branding()
    {
        var engineer = await UserInNewOrganizationAsync(Roles.Engineer);

        (await engineer.PutAsJsonAsync("/api/v1/organization/branding", new { name = "Nope", brandColor = "#000000" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await engineer.PutAsync("/api/v1/organization/logo", LogoUpload(Png))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#B8893B;background:url(//evil)")]
    public async Task A_colour_that_is_not_a_hex_colour_is_rejected(string color)
    {
        var head = await UserInNewOrganizationAsync();

        var response = await head.PutAsJsonAsync("/api/v1/organization/branding", new { name = "Acme", brandColor = color });

        response.IsSuccessStatusCode.Should().BeFalse();
        (await OrganizationAsync(head)).GetProperty("brandColor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_head_uploads_a_logo_and_everyone_in_the_organization_can_fetch_it()
    {
        var head = await UserInNewOrganizationAsync();
        (await OrganizationAsync(head)).GetProperty("logoVersion").ValueKind.Should().Be(JsonValueKind.Null);
        (await head.GetAsync("/api/v1/organization/logo")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var upload = await head.PutAsync("/api/v1/organization/logo", LogoUpload(Png));
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());

        (await OrganizationAsync(head)).GetProperty("logoVersion").GetString().Should().NotBeNullOrEmpty();
        var logo = await head.GetAsync("/api/v1/organization/logo");
        logo.StatusCode.Should().Be(HttpStatusCode.OK);
        logo.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        logo.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        (await logo.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
    }

    [Fact]
    public async Task A_file_that_is_not_really_an_image_is_rejected_whatever_it_claims_to_be()
    {
        var head = await UserInNewOrganizationAsync();

        var response = await head.PutAsync("/api/v1/organization/logo",
            LogoUpload("<svg onload=alert(1)></svg>"u8.ToArray(), contentType: "image/png"));

        response.IsSuccessStatusCode.Should().BeFalse();
        (await head.GetAsync("/api/v1/organization/logo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_logo_over_the_size_limit_is_rejected()
    {
        var head = await UserInNewOrganizationAsync();
        byte[] tooBig = [.. Png, .. new byte[OrganizationLogo.MaxBytes]];

        (await head.PutAsync("/api/v1/organization/logo", LogoUpload(tooBig))).IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task Removing_the_logo_clears_it()
    {
        var head = await UserInNewOrganizationAsync();
        await head.PutAsync("/api/v1/organization/logo", LogoUpload(Png));

        (await head.DeleteAsync("/api/v1/organization/logo")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrganizationAsync(head)).GetProperty("logoVersion").ValueKind.Should().Be(JsonValueKind.Null);
        (await head.GetAsync("/api/v1/organization/logo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task One_organizations_branding_and_logo_are_not_anothers()
    {
        var acme = await UserInNewOrganizationAsync();
        var other = await UserInNewOrganizationAsync();
        await acme.PutAsJsonAsync("/api/v1/organization/branding", new { name = "Acme Only", brandColor = "#112233" });
        await acme.PutAsync("/api/v1/organization/logo", LogoUpload(Png));

        var otherOrg = await OrganizationAsync(other);
        otherOrg.GetProperty("name").GetString().Should().NotBe("Acme Only");
        otherOrg.GetProperty("brandColor").ValueKind.Should().Be(JsonValueKind.Null);
        (await other.GetAsync("/api/v1/organization/logo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
