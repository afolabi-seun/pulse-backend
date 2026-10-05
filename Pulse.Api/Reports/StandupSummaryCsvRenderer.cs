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

        AppendLine(sb, $"Standup Digest — {summary.Date:yyyy-MM-dd}");
        AppendLine(sb, $"Entries,{summary.Entries.Items.Count}");
        AppendLine(sb, $"Missing,{summary.MissingEngineers.Count}");
        sb.AppendLine();

        // ── Check-ins ─────────────────────────────────────────────────────────
        AppendLine(sb, "=== CHECK-INS ===");
        AppendLine(sb, "Team,Engineer,Role,Project,Completed,Planned Next,Blockers");
        foreach (var e in summary.Entries.Items)
        {
            AppendLine(sb,
                $"{Escape(e.TeamName ?? "No team")},{Escape(e.EngineerName)},{Escape(RoleLabel(e.Role))}," +
                $"{Escape(e.ProjectName ?? "General")},{Escape(e.Completed)},{Escape(e.PlannedNext)},{Escape(e.Blockers ?? "")}");
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
