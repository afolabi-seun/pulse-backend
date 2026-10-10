using Pulse.Domain.Organizations;
using FluentAssertions;

namespace Pulse.UnitTests.Organizations;

public class OrganizationTests
{
    [Fact]
    public void Create_trims_the_name_and_starts_active()
    {
        var org = Organization.Create("  Acme Corp  ", "acme", " billing@acme.test ");

        org.Name.Should().Be("Acme Corp");
        org.Slug.Should().Be("acme");
        org.BillingEmail.Should().Be("billing@acme.test");
        org.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_treats_a_blank_billing_email_as_none()
    {
        Organization.Create("Acme", "acme", "   ").BillingEmail.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_name(string name)
    {
        var act = () => Organization.Create(name, "acme");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("acme")]
    [InlineData("acme-corp-2")]
    public void Valid_slugs_are_accepted(string slug)
    {
        Organization.IsValidSlug(slug).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Acme")]
    [InlineData("acme corp")]
    [InlineData("acme_corp")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ąćme")]
    public void Invalid_slugs_are_rejected(string? slug)
    {
        Organization.IsValidSlug(slug).Should().BeFalse();
    }

    [Fact]
    public void Create_rejects_an_invalid_slug()
    {
        var act = () => Organization.Create("Acme", "Acme Corp");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_51_character_slug_is_too_long()
    {
        Organization.IsValidSlug(new string('a', 50)).Should().BeTrue();
        Organization.IsValidSlug(new string('a', 51)).Should().BeFalse();
    }

    [Fact]
    public void Deactivate_and_reactivate_toggle_IsActive()
    {
        var org = Organization.Create("Acme", "acme");

        org.Deactivate();
        org.IsActive.Should().BeFalse();

        org.Reactivate();
        org.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Rename_rejects_a_blank_name_and_keeps_the_old_one()
    {
        var org = Organization.Create("Acme", "acme");

        var act = () => org.Rename(" ");

        act.Should().Throw<ArgumentException>();
        org.Name.Should().Be("Acme");
    }
}

public class OrganizationBrandingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] WebP = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 1, 2];

    [Theory]
    [InlineData("#B8893B")]
    [InlineData("#b8893b")]
    [InlineData("#000000")]
    public void A_hex_colour_is_accepted_and_stored_uppercase(string color)
    {
        var org = Organization.Create("Acme", "acme");

        org.SetBrandColor(color);

        org.BrandColor.Should().Be(color.ToUpperInvariant());
    }

    [Theory]
    [InlineData("B8893B")]
    [InlineData("#B8893")]
    [InlineData("#B8893BFF")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#B8893B; background:url(x)")]
    public void Anything_but_a_six_digit_hex_colour_is_rejected(string color)
    {
        var org = Organization.Create("Acme", "acme");

        org.Invoking(o => o.SetBrandColor(color)).Should().Throw<ArgumentException>();
        org.BrandColor.Should().BeNull();
    }

    [Fact]
    public void Clearing_the_colour_goes_back_to_the_default()
    {
        var org = Organization.Create("Acme", "acme");
        org.SetBrandColor("#B8893B");

        org.SetBrandColor(null);

        org.BrandColor.Should().BeNull();
    }

    [Fact]
    public void The_image_type_comes_from_the_files_own_bytes()
    {
        OrganizationLogo.DetectContentType(Png).Should().Be("image/png");
        OrganizationLogo.DetectContentType(Jpeg).Should().Be("image/jpeg");
        OrganizationLogo.DetectContentType(WebP).Should().Be("image/webp");
        OrganizationLogo.DetectContentType("<svg onload=alert(1)>"u8).Should().BeNull("SVG can carry script");
        OrganizationLogo.DetectContentType("<html>"u8).Should().BeNull();
    }

    [Fact]
    public void A_logo_whose_bytes_do_not_match_the_claimed_type_is_rejected()
    {
        var act = () => OrganizationLogo.Create(Guid.NewGuid(), "image/png", "<script>alert(1)</script>"u8.ToArray());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_logo_over_the_size_limit_is_rejected()
    {
        byte[] tooBig = [.. Png, .. new byte[OrganizationLogo.MaxBytes]];

        var act = () => OrganizationLogo.Create(Guid.NewGuid(), "image/png", tooBig);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Changing_or_removing_the_logo_updates_its_version()
    {
        var org = Organization.Create("Acme", "acme");
        org.LogoUpdatedAt.Should().BeNull();

        org.MarkLogoChanged(hasLogo: true);
        org.LogoUpdatedAt.Should().NotBeNull();

        org.MarkLogoChanged(hasLogo: false);
        org.LogoUpdatedAt.Should().BeNull();
    }
}
