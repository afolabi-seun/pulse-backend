using Pulse.Application.Common;
using FluentAssertions;

namespace Pulse.UnitTests.Common;

public class DescriptionSanitizerTests
{
    [Fact]
    public void Sanitize_strips_script_tags()
    {
        var result = DescriptionSanitizer.Sanitize("<p>Hello</p><script>alert('xss')</script>");

        result.Should().Contain("Hello").And.NotContain("script").And.NotContain("alert");
    }

    [Fact]
    public void Sanitize_strips_event_handler_attributes()
    {
        var result = DescriptionSanitizer.Sanitize("<p onclick=\"alert('xss')\">Hello</p>");

        result.Should().NotContain("onclick").And.NotContain("alert");
    }

    [Fact]
    public void Sanitize_strips_disallowed_tags_but_keeps_their_text()
    {
        var result = DescriptionSanitizer.Sanitize("<iframe src=\"evil.com\"></iframe><p>Safe text</p>");

        result.Should().Contain("Safe text").And.NotContain("iframe");
    }

    [Fact]
    public void Sanitize_keeps_allowed_formatting_tags()
    {
        var result = DescriptionSanitizer.Sanitize("<p><strong>Bold</strong></p><ul><li>Item</li></ul>");

        result.Should().Contain("<strong>Bold</strong>").And.Contain("<ul>").And.Contain("<li>Item</li>");
    }

    [Fact]
    public void Sanitize_forces_safe_rel_and_target_on_links()
    {
        var result = DescriptionSanitizer.Sanitize("<a href=\"https://example.com\" target=\"_self\">link</a>");

        result.Should().Contain("target=\"_blank\"").And.Contain("rel=\"noopener noreferrer\"");
    }

    [Fact]
    public void Sanitize_strips_javascript_scheme_links()
    {
        var result = DescriptionSanitizer.Sanitize("<a href=\"javascript:alert(1)\">click</a>");

        result.Should().NotContain("javascript:");
    }

    [Fact]
    public void Sanitize_returns_null_for_null_input()
    {
        DescriptionSanitizer.Sanitize(null).Should().BeNull();
    }

    [Fact]
    public void SanitizePlainText_converts_newlines_to_br_so_they_survive_HTML_rendering()
    {
        var result = DescriptionSanitizer.SanitizePlainText("Line one\nLine two\nLine three");

        result.Should().Contain("Line one<br>Line two<br>Line three");
    }

    [Fact]
    public void SanitizePlainText_escapes_stray_angle_brackets_instead_of_treating_them_as_markup()
    {
        // e.g. a CSV description containing "value < 100" must not be parsed as a broken tag.
        var result = DescriptionSanitizer.SanitizePlainText("Only valid if value < 100 && flag > 0");

        result.Should().Contain("&lt;").And.Contain("&amp;").And.Contain("&gt;");
        result.Should().NotContain("< 100");
    }

    [Fact]
    public void SanitizePlainText_returns_null_for_null_input()
    {
        DescriptionSanitizer.SanitizePlainText(null).Should().BeNull();
    }
}
