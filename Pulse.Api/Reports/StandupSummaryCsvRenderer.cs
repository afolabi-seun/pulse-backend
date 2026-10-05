using System.Text;
using Pulse.Application.CheckIns.Queries;

namespace Pulse.Api.Reports;

/// <summary>
/// Renders a StandupSummaryDto to a CSV byte array with multiple labelled sections.
/// Uses plain StringBuilder — no external libraries required, mirroring PmoReportCsvRenderer.
/// </summary>
public static class StandupSummaryCsvRenderer
{
    public static byte[] Render(StandupSummaryDto summary)
    {
        var sb = new StringBuilder();

        // Entries are per check-in (one engineer can submit one per project); rows below are per
        // engineer, so an engineer with several check-ins the same day is one row with all of
        // their projects' work context combined — the two counts below can legitimately differ.
        var byEngineer = summary.Entries.Items
            .GroupBy(e => e.EngineerId)
            .ToList();

        AppendLine(sb, $"Standup Digest — {summary.Date:yyyy-MM-dd}");
        AppendLine(sb, $"Engineers checked in,{byEngineer.Count}");
        AppendLine(sb, $"Total check-ins,{summary.Entries.Items.Count}");
        AppendLine(sb, $"Missing,{summary.MissingEngineers.Count}");
        sb.AppendLine();

        // ── Check-ins ─────────────────────────────────────────────────────────
        AppendLine(sb, "=== CHECK-INS ===");
        AppendLine(sb, "Team,Engineer,Role,Projects,Completed,Planned Next,Blockers");
        foreach (var items in byEngineer)
        {
            var list = items.ToList();
            var first = list[0];
            var projects = string.Join("; ", list.Select(ProjectLabel));

            // A single check-in reads exactly as it did before (no project prefix clutter); two or
            // more are tagged "[Project] text" and joined, so nothing is dropped when combined.
            string Combine(Func<StandupEntryDto, string?> select)
            {
                if (list.Count == 1) return select(first) ?? "";
                var parts = list
                    .Where(e => !string.IsNullOrEmpty(select(e)))
                    .Select(e => $"[{ProjectLabel(e)}] {select(e)}");
                return string.Join(" | ", parts);
            }

            AppendLine(sb,
                $"{Escape(first.TeamName ?? "No team")},{Escape(first.EngineerName)},{Escape(RoleLabel(first.Role))}," +
                $"{Escape(projects)},{Escape(Combine(e => e.Completed))},{Escape(Combine(e => e.PlannedNext))},{Escape(Combine(e => e.Blockers))}");
        }
        sb.AppendLine();

        // ── Missing ───────────────────────────────────────────────────────────
        AppendLine(sb, "=== MISSING ===");
        AppendLine(sb, "Engineer");
        foreach (var m in summary.MissingEngineers)
        {
            AppendLine(sb, Escape(m.Name));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string ProjectLabel(StandupEntryDto e) => e.ProjectName ?? "General";

    private static string RoleLabel(string role) =>
        role.Replace('_', ' ');

    private static void AppendLine(StringBuilder sb, string line) => sb.AppendLine(line);

    /// <summary>Wraps a cell value in double quotes and escapes any inner double quotes.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
