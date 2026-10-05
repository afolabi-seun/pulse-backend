using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Pulse.Api.Wiki;

/// <summary>
/// Renders a wiki page's markdown content to a PDF byte array using Markdig (parsing) and
/// QuestPDF (layout) — same rendering stack LeadershipReportPdfRenderer uses, just walking a
/// markdown AST instead of composing a fixed report shape. Lives in the API project for the same
/// reason: PDF rendering is a presentation concern.
/// </summary>
public static class WikiPagePdfRenderer
{
    static WikiPagePdfRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // Advanced extensions covers GFM tables, task lists, and auto-links — the same feature set
    // the frontend's remark-gfm plugin renders on screen, so a page exported to PDF doesn't lose
    // structure the author actually used.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static byte[] Render(string title, string? markdown)
    {
        var document = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Arial"));

                page.Header().PaddingBottom(10).Column(col =>
                {
                    col.Item().Text(title).FontSize(20).Bold();
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    foreach (var block in document)
                        RenderBlock(col, block);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Pulse Wiki · ").FontColor(Colors.Grey.Medium);
                    t.Span(DateTime.UtcNow.ToString("dd MMM yyyy") + " · Page ").FontColor(Colors.Grey.Medium);
                    t.CurrentPageNumber().FontColor(Colors.Grey.Medium);
                    t.Span(" of ").FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf();
    }

    private static void RenderBlock(ColumnDescriptor col, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var size = heading.Level switch { 1 => 17, 2 => 14, 3 => 12, _ => 11 };
                col.Item().PaddingTop(heading.Level <= 2 ? 8 : 4).Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(size).Bold());
                    RenderInlines(t, heading.Inline, bold: false, italic: false);
                });
                break;

            case ParagraphBlock paragraph:
                col.Item().Text(t => RenderInlines(t, paragraph.Inline, bold: false, italic: false));
                break;

            case QuoteBlock quote:
                col.Item().PaddingLeft(10).BorderLeft(2).BorderColor(Colors.Grey.Lighten1)
                    .PaddingLeft(8).Column(qc =>
                    {
                        qc.Spacing(4);
                        foreach (var child in quote)
                            RenderBlock(qc, child);
                    });
                break;

            case FencedCodeBlock or CodeBlock:
                var codeText = ((LeafBlock)block).Lines.ToString();
                col.Item().Background(Colors.Grey.Lighten4).Padding(8)
                    .Text(codeText).FontFamily("Courier New").FontSize(9);
                break;

            case ListBlock list:
                RenderList(col, list);
                break;

            case Table table:
                RenderTable(col, table);
                break;

            case ThematicBreakBlock:
                col.Item().PaddingVertical(2).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                break;

            case LeafBlock { Inline: not null } leaf:
                col.Item().Text(t => RenderInlines(t, leaf.Inline, bold: false, italic: false));
                break;
        }
    }

    private static void RenderList(ColumnDescriptor col, ListBlock list)
    {
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            col.Item().Row(row =>
            {
                row.ConstantItem(16).Text(list.IsOrdered ? $"{index}." : "•");
                row.RelativeItem().Column(ic =>
                {
                    ic.Spacing(2);
                    foreach (var itemBlock in item)
                        RenderBlock(ic, itemBlock);
                });
            });
            index++;
        }
    }

    private static void RenderTable(ColumnDescriptor col, Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return;
        var columnCount = rows[0].Count;

        col.Item().PaddingVertical(4).Table(t =>
        {
            t.ColumnsDefinition(cols =>
            {
                for (var i = 0; i < columnCount; i++)
                    cols.RelativeColumn();
            });

            for (var r = 0; r < rows.Count; r++)
            {
                var isHeader = r == 0 && rows[r].IsHeader;
                foreach (var cellBlock in rows[r])
                {
                    if (cellBlock is not TableCell cell) continue;
                    var container = t.Cell().Border(1).BorderColor(Colors.Grey.Lighten2)
                        .Background(isHeader ? Colors.Grey.Lighten3 : Colors.White)
                        .Padding(5);

                    container.Column(cc =>
                    {
                        foreach (var cellChild in cell)
                            RenderBlock(cc, cellChild);
                    });
                }
            }
        });
    }

    private static void RenderInlines(TextDescriptor t, ContainerInline? inlines, bool bold, bool italic)
    {
        if (inlines is null) return;
        foreach (var inline in inlines)
            RenderInline(t, inline, bold, italic);
    }

    private static void RenderInline(TextDescriptor t, Inline inline, bool bold, bool italic)
    {
        switch (inline)
        {
            case LiteralInline lit:
                {
                    var span = t.Span(lit.Content.ToString());
                    if (bold) span.Bold();
                    if (italic) span.Italic();
                    break;
                }

            case EmphasisInline emphasis:
                // A single delimiter (*text*) is italic, doubled (**text**) is bold — Markdig
                // exposes this as DelimiterCount rather than a Bold/Italic flag.
                var isBold = emphasis.DelimiterCount >= 2;
                foreach (var child in emphasis)
                    RenderInline(t, child, bold || isBold, italic || !isBold);
                break;

            case CodeInline code:
                {
                    var span = t.Span(code.Content).FontFamily("Courier New").BackgroundColor(Colors.Grey.Lighten4);
                    if (bold) span.Bold();
                    if (italic) span.Italic();
                    break;
                }

            case LinkInline link:
                var linkText = string.Concat(link.Select(FlattenInlineText));
                if (string.IsNullOrEmpty(linkText)) linkText = link.Url ?? string.Empty;
                t.Span(linkText).FontColor(Colors.Blue.Darken1).Underline();
                // This QuestPDF version's text spans aren't independently clickable (Hyperlink is
                // an IContainer-level wrapper, not a span style), so the URL itself is appended
                // for an absolute link — otherwise a reader has the label but no way to actually
                // reach it from a static PDF. A relative "./other-page.md" wiki-internal link
                // (see WikiContent.tsx's own resolution logic) has nothing to resolve to outside
                // the app either way, so it's left as styled text with no URL appended.
                if (link.Url is { } url && (url.StartsWith("http://") || url.StartsWith("https://")) && url != linkText)
                    t.Span($" ({url})").FontSize(8).FontColor(Colors.Grey.Darken1);
                break;

            case LineBreakInline:
                t.Span(" ");
                break;

            case ContainerInline container:
                foreach (var child in container)
                    RenderInline(t, child, bold, italic);
                break;
        }
    }

    private static string FlattenInlineText(Inline inline) => inline switch
    {
        LiteralInline lit => lit.Content.ToString(),
        ContainerInline container => string.Concat(container.Select(FlattenInlineText)),
        _ => string.Empty,
    };
}
