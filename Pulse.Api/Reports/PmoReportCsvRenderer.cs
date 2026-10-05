using System.Text;
using Pulse.Application.Reports;

namespace Pulse.Api.Reports;

/// <summary>
/// Renders a PmoReportDto to a CSV byte array with multiple labelled sections.
/// Uses plain StringBuilder — no external libraries required.
/// </summary>
public static class PmoReportCsvRenderer
{
    public static byte[] Render(PmoReportDto report)
    {
        var sb = new StringBuilder();

        AppendLine(sb, $"PMO Report — Week of {report.WeekOf}");
        // Delivered points (and every Hours figure below) reflect DeliveredFrom/DeliveredTo, which
        // only equals WeekOf's own week when no custom range was requested — called out explicitly
        // here rather than left implied by the "Week of" line above, which they no longer match.
        AppendLine(sb, $"Delivered/Hours Window,{report.DeliveredFrom} to {report.DeliveredTo}");
        AppendLine(sb, $"Total Delivered Points,{report.TotalDeliveredPoints}");
        AppendLine(sb, $"Previous Week Points,{report.PreviousWeekPoints}");
        sb.AppendLine();

        // ── Team Utilization ──────────────────────────────────────────────────
        // Column set mirrors EngineerUtilizationTable.tsx (the shared on-screen table) so the
        // export isn't missing fields a reader just saw on screen — In QA is split into Tasks/Points
        // (rather than the UI's combined "1 (3 pts)") since a CSV cell is for machine parsing.
        AppendLine(sb, "=== TEAM UTILIZATION ===");
        AppendLine(sb, "Team,Engineer,Role,Active Tasks,Points,Due This Cycle,In QA Tasks,In QA Points,Baseline,Load %,Blockers,Check-ins This Week,Completed,Subtasks Done,Overworked,Hours This Week");
        foreach (var team in report.Teams)
        {
            foreach (var eng in team.Engineers)
            {
                var loadPct = eng.BaselinePoints > 0
                    ? (int)Math.Round((double)eng.TotalPoints / eng.BaselinePoints * 100)
                    : 0;
                AppendLine(sb,
                    $"{Escape(team.TeamName)},{Escape(eng.Name)},{Escape(eng.Role)}," +
                    $"{eng.ActiveTasks},{eng.TotalPoints},{eng.CyclePoints},{eng.TasksInQa},{eng.PointsInQa},{eng.BaselinePoints}," +
                    $"{loadPct},{eng.Blockers},{eng.CheckInsThisWeek},{eng.CompletedTasks},{eng.SubtasksCompleted}," +
                    $"{(eng.IsOverworked ? "Yes" : "No")},{eng.HoursLoggedThisWeek}");
            }
        }
        sb.AppendLine();

        // ── Project Health ────────────────────────────────────────────────────
        AppendLine(sb, "=== PROJECT HEALTH ===");
        AppendLine(sb, "Project,Active Tasks,Blocked,Done This Sprint,Total Tasks,Completion %,Escalations,High Priority Open,Active Sprint,Health,Hours This Week");
        foreach (var p in report.Projects)
        {
            AppendLine(sb,
                $"{Escape(p.Name)},{p.ActiveTasks},{p.BlockedTasks},{p.DoneThisSprint}," +
                $"{p.TotalTasks},{p.CompletionPct}," +
                $"{p.EscalationCount},{p.HighPriorityOpenTasks},{Escape(p.ActiveSprintName ?? "—")},{Escape(p.Health)},{p.HoursLoggedThisWeek}");
        }
        sb.AppendLine();

        // ── Sprint Velocity ───────────────────────────────────────────────────
        AppendLine(sb, "=== SPRINT DELIVERY RATE ===");
        AppendLine(sb, "Team,Sprint,Planned Points,Delivered Points,Rate %");
        foreach (var team in report.SprintVelocity)
        {
            foreach (var sprint in team.Sprints)
            {
                var rate = sprint.PlannedPoints.HasValue && sprint.PlannedPoints.Value > 0
                    ? (int)Math.Round((double)sprint.DeliveredPoints / sprint.PlannedPoints.Value * 100)
                    : 0;
                var rateFmt = sprint.PlannedPoints.HasValue ? rate.ToString() : "—";
                AppendLine(sb,
                    $"{Escape(team.TeamName)},{Escape(sprint.SprintName)}," +
                    $"{sprint.PlannedPoints?.ToString() ?? "—"},{sprint.DeliveredPoints},{rateFmt}");
            }
        }
        sb.AppendLine();

        // ── Check-in Compliance ───────────────────────────────────────────────
        AppendLine(sb, "=== CHECK-IN COMPLIANCE ===");
        AppendLine(sb, "Team,Week Of,Engineers,Checked In,Compliance %");
        foreach (var team in report.CheckInCompliance)
        {
            foreach (var week in team.Weeks)
            {
                AppendLine(sb,
                    $"{Escape(team.TeamName)},{Escape(week.WeekOf)}," +
                    $"{week.EngineerCount},{week.CheckedInCount},{week.CompliancePct}");
            }
        }
        sb.AppendLine();

        // ── Active Blockers ───────────────────────────────────────────────────
        if (report.BlockerAging.Count > 0)
        {
            AppendLine(sb, "=== ACTIVE BLOCKERS ===");
            AppendLine(sb, "Title,Assignee,Project,Days Blocked,Reason");
            foreach (var b in report.BlockerAging)
            {
                AppendLine(sb,
                    $"{Escape(b.Title)},{Escape(b.AssigneeName ?? "Unassigned")}," +
                    $"{Escape(b.ProjectName ?? "—")},{b.DaysBlocked},{Escape(b.Reason ?? "")}");
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
