using Ganss.Xss;

namespace Pulse.Application.Common;

/// <summary>
/// Sanitizes task description HTML from the rich-text editor before it's persisted — the one
/// point every write path (create, update, bulk-create, CSV import) funnels through, so a
/// caller can never forget to sanitize. Strips scripts, event handlers, and anything outside a
/// small formatting allowlist; a plain-text description (e.g. from CSV import, or written before
/// the rich editor existed) passes through with any stray "&lt;"/"&amp;" safely escaped, so it
/// still renders correctly as plain text rather than being misparsed as HTML.
/// </summary>
public static class DescriptionSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        return Sanitizer.Sanitize(html);
    }

    /// <summary>
    /// For genuinely plain text (CSV import, or any other non-editor source) rather than HTML
    /// from the rich-text editor. HTML-encodes the raw text first — so a stray "&lt;"/"&amp;"
    /// can't be misread as markup — then turns real newlines into &lt;br&gt; before sanitizing;
    /// otherwise line breaks a CSV cell relied on would silently collapse when rendered as HTML.
    /// </summary>
    public static string? SanitizePlainText(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return plainText;

        var withLineBreaks = System.Net.WebUtility.HtmlEncode(plainText)
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>");

        return Sanitize(withLineBreaks);
    }

    private static HtmlSanitizer Build()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "strong", "b", "em", "i", "u", "s", "strike",
            "ul", "ol", "li", "h1", "h2", "h3", "blockquote", "code", "pre", "a",
        })
            sanitizer.AllowedTags.Add(tag);

        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.Add("href");

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        // Every link opens safely regardless of what the original href/target claimed.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not AngleSharp.Dom.IElement el || el.TagName.ToLowerInvariant() != "a")
                return;
            el.SetAttribute("target", "_blank");
            el.SetAttribute("rel", "noopener noreferrer");
        };

        return sanitizer;
    }
}
