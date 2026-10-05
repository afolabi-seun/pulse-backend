using System.Text;

namespace Pulse.Application.Reports;

/// <summary>
/// Builds a deterministic first-draft for the 4 free-text Weekly Report fields, purely from data
/// WeeklyReportAssembler has already computed — no LLM, no extra I/O. Deliberately does not invent
/// information the system has no record of (individual delivered items, headcount/hiring changes);
/// where that would be needed, the draft states what IS known (workstream completion %, team load)
/// and leaves the rest for the team lead to fill in by hand.
/// </summary>
public static class WeeklyReportNarrativeGenerator
{
    public static string ExecutiveSummary(
        int totalDelivered, int previousWeekPoints,
        IReadOnlyList<ProjectHealthDto> workstreams, IReadOnlyList<BlockerAgingDto> blockers,
        TeamUtilizationDto utilization)
    {
        var sb = new StringBuilder();

        var deltaPct = previousWeekPoints > 0
            ? (int)Math.Round((double)(totalDelivered - previousWeekPoints) / previousWeekPoints * 100)
            : (int?)null;
        sb.Append($"Delivered {totalDelivered} point{(totalDelivered == 1 ? "" : "s")} this week");
        if (deltaPct is not null)
            sb.Append($" ({(deltaPct >= 0 ? "+" : "")}{deltaPct}% vs last week)");
        sb.Append('.');

        if (workstreams.Count > 0)
        {
            var onTrack = workstreams.Count(w => w.Health == "Healthy");
            var atRisk = workstreams.Count(w => w.Health == "AtRisk");
            var critical = workstreams.Count(w => w.Health == "Critical");
            sb.Append($" Across {workstreams.Count} workstream{(workstreams.Count == 1 ? "" : "s")}" +
                      $" ({onTrack} on track, {atRisk} at risk, {critical} delayed).");
        }

        sb.Append(utilization.AvgLoadPct is int loadPct ? $" Team load is at {loadPct}%" : " Team load can't be measured (no one currently on this team)");
        sb.Append(utilization.OverworkedCount > 0
            ? $" with {utilization.OverworkedCount} engineer{(utilization.OverworkedCount == 1 ? "" : "s")} flagged as overworked."
            : ".");

        if (blockers.Count > 0)
            sb.Append($" {blockers.Count} active blocker{(blockers.Count == 1 ? "" : "s")} need{(blockers.Count == 1 ? "s" : "")} attention.");

        return sb.ToString();
    }

    /// <summary>" — 2 overdue tasks; 1 blocked 5+ working days (oldest 7)": why a workstream isn't on track,
    /// from the same reasons the report's Health column shows. Empty for a healthy one.</summary>
    private static string Why(ProjectHealthDto w) =>
        w.Reasons is { Count: > 0 } ? " — " + string.Join("; ", w.Reasons.Select(r => r.Text)) : string.Empty;

    public static string KeyAccomplishments(IReadOnlyList<ProjectHealthDto> workstreams)
    {
        if (workstreams.Count == 0)
            return "No workstreams owned by this team this week.";

        var statusLabel = new Func<string, string>(health => health switch
        {
            "Healthy" => "on track",
            "AtRisk" => "at risk",
            "Critical" => "delayed",
            _ => health,
        });

        return string.Join('\n', workstreams.Select(w =>
            $"- {w.Name}: {w.CompletionPct}% complete, {statusLabel(w.Health)}{Why(w)}"));
    }

    public static string PlannedNextWeek(IReadOnlyList<ProjectHealthDto> workstreams, IReadOnlyList<BlockerAgingDto> blockers)
    {
        var lines = new List<string>();

        lines.AddRange(workstreams
            .Where(w => w.Health is "AtRisk" or "Critical")
            .Select(w => $"- Continue work on {w.Name} ({w.CompletionPct}% complete, currently {(w.Health == "Critical" ? "delayed" : "at risk")}{Why(w)})"));

        lines.AddRange(blockers
            .Select(b => $"- Resolve blocker: {b.Title} (blocked {b.DaysBlocked}d)"));

        return lines.Count > 0
            ? string.Join('\n', lines)
            : "No at-risk workstreams or open blockers; continue current sprint plan.";
    }

    public static string ResourcingNotes(TeamUtilizationDto utilization)
    {
        var loadText = utilization.AvgLoadPct is int loadPct
            ? $"Team load is at {loadPct}%."
            : "Team load can't be measured — no one currently on this team.";

        if (utilization.OverworkedCount == 0)
            return $"{loadText} No resourcing concerns to report this week.";

        return $"{loadText} {utilization.OverworkedCount} engineer{(utilization.OverworkedCount == 1 ? "" : "s")} " +
               "currently flagged as overworked — no other resourcing changes to report.";
    }
}
