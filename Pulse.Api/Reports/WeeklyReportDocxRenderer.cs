using System.Reflection;
using System.Text.RegularExpressions;
using Pulse.Application.Reports;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Pulse.Api.Reports;

/// <summary>
/// Fills the Pulse Weekly Report Template (embedded from docs/templates/) with a WeeklyReportDto,
/// producing an editable .docx. Lives in the API project because rendering is a presentation concern,
/// mirroring LeadershipReportPdfRenderer/PmoReportCsvRenderer.
///
/// The template has no bookmarks or named placeholders — every field is a blank table cell or blank
/// bullet paragraph. This renderer locates sections by matching the exact section-heading text (e.g.
/// "1. EXECUTIVE SUMMARY") and then walks the immediately-following sibling elements, so it survives
/// minor content edits to the template but WILL need a matching update here if a heading's wording,
/// a table's column order, or the section order itself ever changes in the template.
/// </summary>
public static class WeeklyReportDocxRenderer
{
    private const string TemplateResourceName = "Pulse.Api.Reports.Templates.WeeklyReportTemplate.docx";
    private static readonly Regex HeadingPattern = new(@"^\d+\.\s", RegexOptions.Compiled);

    public static byte[] Render(WeeklyReportDto report)
    {
        using var templateStream = LoadTemplate();
        using var memoryStream = new MemoryStream();
        templateStream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        using (var doc = WordprocessingDocument.Open(memoryStream, true))
        {
            var body = doc.MainDocumentPart!.Document.Body!;

            FillHeaderTable(body, report);

            SetSectionParagraph(body, "1. EXECUTIVE SUMMARY", report.ExecutiveSummary);
            FillSectionBullets(body, "2. KEY ACCOMPLISHMENTS THIS WEEK", report.KeyAccomplishments);
            FillWorkstreamTable(body, report);
            FillSectionBullets(body, "4. PLANNED FOR NEXT WEEK", report.PlannedNextWeek);
            FillRisksTable(body, report);
            FillKpiTable(body, report);
            SetSectionParagraph(body, "7. TEAM & RESOURCING NOTES", report.ResourcingNotes);
            FillSignOffTable(body, report);

            doc.MainDocumentPart.Document.Save();
        }

        return memoryStream.ToArray();
    }

    private static Stream LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException($"Embedded template '{TemplateResourceName}' not found.");
    }

    // ── Header table (Team/Unit, Team Lead, Reporting Period, Date Submitted, Product/Project, Client(s)) ──

    private static void FillHeaderTable(Body body, WeeklyReportDto report)
    {
        var table = body.Elements<Table>().First();
        var rows = table.Elements<TableRow>().ToList();

        var weekStart = DateOnly.Parse(report.WeekOf);
        var weekEnd = weekStart.AddDays(6);
        var reportingPeriod = $"{weekStart:dd MMM} – {weekEnd:dd MMM yyyy}";

        SetCellText(rows[0], 1, report.TeamName);
        SetCellText(rows[0], 3, report.SubmittedByName ?? string.Empty);
        SetCellText(rows[1], 1, reportingPeriod);
        SetCellText(rows[1], 3, report.SubmittedAt?.ToString("dd MMM yyyy") ?? string.Empty);
        SetCellText(rows[2], 1, string.Empty); // Product/Project — no backing field in Pulse
        SetCellText(rows[2], 3, string.Empty); // Client(s) — no backing field in Pulse
    }

    // ── Free-text paragraphs (Executive Summary, Resourcing Notes) ──

    private static void SetSectionParagraph(Body body, string heading, string text)
    {
        var placeholder = FindHeading(body, heading).ElementsAfter().OfType<Paragraph>().FirstOrDefault();
        if (placeholder is not null)
            SetParagraphText(placeholder, text);
    }

    // ── Bullet lists (Key Accomplishments, Planned Next Week) ──

    private static void FillSectionBullets(Body body, string heading, string text)
    {
        var slots = CollectFollowingParagraphs(FindHeading(body, heading));
        if (slots.Count == 0) return;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < slots.Count && i < lines.Length; i++)
            SetParagraphText(slots[i], lines[i]);

        var last = slots[^1];
        for (var i = slots.Count; i < lines.Length; i++)
        {
            var clone = (Paragraph)last.CloneNode(true);
            last.InsertAfterSelf(clone);
            SetParagraphText(clone, lines[i]);
            last = clone;
        }
    }

    // ── Workstream / Deliverable Status table ──

    private static void FillWorkstreamTable(Body body, WeeklyReportDto report)
    {
        var table = FindHeading(body, "3. WORKSTREAM / DELIVERABLE STATUS").ElementsAfter().OfType<Table>().First();
        FillRepeatingTable(table, report.Workstreams, (row, w) =>
        {
            SetCellText(row, 0, w.Name);
            SetCellText(row, 1, string.Empty); // Owner — no backing field
            SetCellText(row, 2, WorkstreamStatus(w));
            SetCellText(row, 3, string.Empty); // Target Date — no backing field
        });
    }

    private static string WorkstreamStatus(ProjectHealthDto w) => w switch
    {
        { CompletionPct: 100 } => "Complete",
        { Health: "Healthy" } => "On Track",
        { Health: "AtRisk" } => "At Risk",
        _ => "Delayed",
    };

    // ── Risks, Blockers & Support Needed table ──

    private static void FillRisksTable(Body body, WeeklyReportDto report)
    {
        var table = FindHeading(body, "5. RISKS, BLOCKERS & SUPPORT NEEDED").ElementsAfter().OfType<Table>().First();
        FillRepeatingTable(table, report.Blockers, (row, b) =>
        {
            SetCellText(row, 0, b.Title);
            SetCellText(row, 1, string.Empty); // Impact — no backing field anywhere in Pulse
            SetCellText(row, 2, b.Reason ?? string.Empty);
            SetCellText(row, 3, b.AssigneeName ?? string.Empty);
        });
    }

    // ── Key Metrics / KPIs table (fixed 4 rows, not per-item) ──

    private static void FillKpiTable(Body body, WeeklyReportDto report)
    {
        var table = FindHeading(body, "6. KEY METRICS / KPIS").ElementsAfter().OfType<Table>().First();
        var rows = table.Elements<TableRow>().Skip(1).ToList();
        if (rows.Count < 4) return;

        var weeks = report.Compliance.Weeks;
        var complianceThisWeek = weeks.Count > 0 ? $"{weeks[^1].CompliancePct}%" : "—";
        var complianceLastWeek = weeks.Count > 1 ? $"{weeks[^2].CompliancePct}%" : "—";

        SetKpiRow(rows[0], "Delivered Points", report.TotalDeliveredPoints.ToString(), report.PreviousWeekPoints.ToString(), string.Empty);
        SetKpiRow(rows[1], "Check-in Compliance", complianceThisWeek, complianceLastWeek, "100%");
        var teamLoad = report.Utilization.AvgLoadPct is int loadPct ? $"{loadPct}%" : "N/A";
        SetKpiRow(rows[2], "Team Load", teamLoad, string.Empty, "100%");
        SetKpiRow(rows[3], "Overworked Engineers", report.Utilization.OverworkedCount.ToString(), string.Empty, "0");
    }

    private static void SetKpiRow(TableRow row, string metric, string thisWeek, string lastWeek, string target)
    {
        SetCellText(row, 0, metric);
        SetCellText(row, 1, thisWeek);
        SetCellText(row, 2, lastWeek);
        SetCellText(row, 3, target);
    }

    // ── Sign-off table ──

    private static void FillSignOffTable(Body body, WeeklyReportDto report)
    {
        var table = FindHeading(body, "8. SIGN-OFF").ElementsAfter().OfType<Table>().First();
        var row = table.Elements<TableRow>().First();
        SetCellText(row, 1, report.SubmittedByName ?? string.Empty);
        SetCellText(row, 3, report.SubmittedAt?.ToString("dd MMM yyyy") ?? string.Empty);
    }

    // ── Shared helpers ──

    private static Paragraph FindHeading(Body body, string headingText) =>
        body.Elements<Paragraph>().First(p => GetParagraphText(p).Trim() == headingText);

    /// <summary>Following sibling paragraphs up to (not including) the next section heading or table.</summary>
    private static List<Paragraph> CollectFollowingParagraphs(Paragraph heading)
    {
        var result = new List<Paragraph>();
        foreach (var element in heading.ElementsAfter())
        {
            if (element is Table) break;
            if (element is Paragraph p)
            {
                if (HeadingPattern.IsMatch(GetParagraphText(p).Trim())) break;
                result.Add(p);
            }
        }
        return result;
    }

    private static void FillRepeatingTable<T>(Table table, IReadOnlyList<T> items, Action<TableRow, T> fillRow)
    {
        var slots = table.Elements<TableRow>().Skip(1).ToList();
        if (slots.Count == 0) return;

        for (var i = 0; i < slots.Count && i < items.Count; i++)
            fillRow(slots[i], items[i]);

        var last = slots[^1];
        for (var i = slots.Count; i < items.Count; i++)
        {
            var clone = (TableRow)last.CloneNode(true);
            last.InsertAfterSelf(clone);
            fillRow(clone, items[i]);
            last = clone;
        }
    }

    private static void SetCellText(TableRow row, int cellIndex, string value)
    {
        var cell = row.Elements<TableCell>().ElementAt(cellIndex);
        var paragraph = cell.Elements<Paragraph>().FirstOrDefault();
        if (paragraph is not null)
            SetParagraphText(paragraph, value);
    }

    private static string GetParagraphText(Paragraph p) =>
        string.Concat(p.Descendants<Text>().Select(t => t.Text));

    private static void SetParagraphText(Paragraph paragraph, string value)
    {
        var existingRunProps = paragraph.Elements<Run>().FirstOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
        paragraph.RemoveAllChildren<Run>();

        var run = new Run();
        if (existingRunProps is not null)
            run.RunProperties = existingRunProps;
        run.AppendChild(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(run);
    }
}
