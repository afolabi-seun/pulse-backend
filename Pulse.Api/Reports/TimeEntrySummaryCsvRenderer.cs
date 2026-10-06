using System.Text;
using Pulse.Application.TimeEntries.Queries;

namespace Pulse.Api.Reports;

/// <summary>
/// Renders a TimeEntrySummaryDto to a CSV byte array — the by-engineer daily breakdown and the
/// by-project roll-up, same two sections the Time Summary page itself shows. Uses plain
/// StringBuilder, matching PmoReportCsvRenderer's own approach — no external library required.
/// </summary>
public static class TimeEntrySummaryCsvRenderer
{
    /// <param name="summary">The by-engineer and by-project roll-ups.</param>
    /// <param name="details">One entry per line in <paramref name="summary"/>.Projects, same order —
    /// the per-task/per-category breakdown each line shows when expanded on screen. Omit (or pass
    /// an empty list) to render just the two roll-up sections, as before.</param>
    public static byte[] Render(TimeEntrySummaryDto summary, IReadOnlyList<ProjectTimeActivityDto>? details = null)
    {
        var sb = new StringBuilder();

        AppendLine(sb, $"Time Summary,{summary.WeekOf} to {summary.To}");
        AppendLine(sb, $"Total Hours,{summary.TeamTotalHours}");
        sb.AppendLine();

        // ── Hours by Engineer ─────────────────────────────────────────────────
        // One column per day in the window — every engineer's DailyHours covers the exact same
        // dates in the exact same order, so the first engineer's (or an empty list, if nobody
        // logged anything) is as good a source for the header as any.
        var days = summary.Engineers.Count > 0
            ? summary.Engineers[0].DailyHours.Select(d => d.Date).ToList()
            : new List<DateOnly>();

        AppendLine(sb, "=== HOURS BY ENGINEER ===");
        AppendLine(sb, string.Join(',', new[] { "Engineer" }.Concat(days.Select(d => d.ToString("yyyy-MM-dd"))).Concat(new[] { "Total Hours" })));
        foreach (var eng in summary.Engineers)
        {
            var hoursByDate = eng.DailyHours.ToDictionary(d => d.Date, d => d.Hours);
            var cells = new[] { Escape(eng.Name) }
                .Concat(days.Select(d => hoursByDate.GetValueOrDefault(d, 0m).ToString()))
                .Concat(new[] { eng.TotalHours.ToString() });
            AppendLine(sb, string.Join(',', cells));
        }
        sb.AppendLine();

        // ── Hours by Project ──────────────────────────────────────────────────
        AppendLine(sb, "=== HOURS BY PROJECT ===");
        AppendLine(sb, "Project,Kind,Total Hours");
        foreach (var p in summary.Projects)
            AppendLine(sb, $"{Escape(p.ProjectName)},{Escape(p.Kind)},{p.TotalHours}");

        // ── Hours by Category/Task ───────────────────────────────────────────
        // Same breakdown as the "Hours by project" detail on screen — a category row's label
        // already carries its note when it has one (e.g. "Meetings — Sprint planning"), so two
        // different meetings don't collapse into one undifferentiated line here either. Empty for
        // the personal-tasks line, which names people but never task/category detail.
        if (details is { Count: > 0 })
        {
            sb.AppendLine();
            AppendLine(sb, "=== HOURS BY CATEGORY/TASK ===");
            AppendLine(sb, "Project,Category/Task,Who Logged,Hours");
            foreach (var project in details)
            {
                foreach (var item in project.Items)
                {
                    var whoLogged = string.Join(" | ", item.People.Select(p => $"{p.Name} {p.Hours}h"));
                    AppendLine(sb, $"{Escape(project.Name)},{Escape(item.Label)},{Escape(whoLogged)},{item.Hours}");
                }
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendLine(StringBuilder sb, string line) => sb.AppendLine(line);

    /// <summary>Wraps a cell value in double quotes and escapes any inner double quotes.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
