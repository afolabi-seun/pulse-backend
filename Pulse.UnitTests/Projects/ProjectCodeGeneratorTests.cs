using Pulse.Application.Projects;
using FluentAssertions;

namespace Pulse.UnitTests.Projects;

public class ProjectCodeGeneratorTests
{
    [Theory]
    [InlineData("Notifications Platform", "NOTIFI")]
    [InlineData("OMS", "OMS")]
    [InlineData("api-modernisation", "APIMOD")]
    [InlineData("  Regulatory Reporting  ", "REGULA")]
    public void DeriveBase_uppercases_strips_punctuation_and_caps_at_6_chars(string name, string expected)
    {
        ProjectCodeGenerator.DeriveBase(name).Should().Be(expected);
    }

    [Fact]
    public void DeriveBase_falls_back_to_PROJ_for_a_name_with_no_letters_or_digits()
    {
        ProjectCodeGenerator.DeriveBase("--- ***").Should().Be("PROJ");
    }

    [Fact]
    public void MakeUnique_returns_the_base_code_when_not_taken()
    {
        var existing = new HashSet<string>();

        var code = ProjectCodeGenerator.MakeUnique("NOTIF", existing);

        code.Should().Be("NOTIF");
        existing.Should().Contain("NOTIF");
    }

    [Fact]
    public void MakeUnique_appends_a_numeric_suffix_on_collision()
    {
        var existing = new HashSet<string> { "NOTIF" };

        var code = ProjectCodeGenerator.MakeUnique("NOTIF", existing);

        code.Should().Be("NOTIF2");
    }

    [Fact]
    public void MakeUnique_skips_past_multiple_taken_suffixes()
    {
        var existing = new HashSet<string> { "NOTIF", "NOTIF2", "NOTIF3" };

        var code = ProjectCodeGenerator.MakeUnique("NOTIF", existing);

        code.Should().Be("NOTIF4");
    }

    [Fact]
    public void MakeUnique_across_repeated_calls_never_reuses_a_code_it_already_handed_out()
    {
        var existing = new HashSet<string>();

        var first  = ProjectCodeGenerator.MakeUnique("NOTIF", existing);
        var second = ProjectCodeGenerator.MakeUnique("NOTIF", existing);
        var third  = ProjectCodeGenerator.MakeUnique("NOTIF", existing);

        new[] { first, second, third }.Should().OnlyHaveUniqueItems();
    }
}
