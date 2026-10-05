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
