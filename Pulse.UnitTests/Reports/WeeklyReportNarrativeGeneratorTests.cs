using Pulse.Application.Reports;
using FluentAssertions;

namespace Pulse.UnitTests.Reports;

public class WeeklyReportNarrativeGeneratorTests
{
    private static ProjectHealthDto Workstream(string name, int completionPct, string health) =>
        new(Guid.NewGuid(), name, ActiveTasks: 1, BlockedTasks: 0, DoneThisSprint: 0,
            TotalTasks: 5, CompletionPct: completionPct, EscalationCount: 0, ActiveSprintName: null, Health: health,
            HoursLoggedThisWeek: 0);

    private static BlockerAgingDto Blocker(string title, int daysBlocked) =>
        new(Guid.NewGuid(), title, AssigneeName: "Someone", ProjectName: "A Project", Reason: "waiting on X", DaysBlocked: daysBlocked);

    private static TeamUtilizationDto Utilization(int? avgLoadPct, int overworkedCount) =>
        new(Guid.NewGuid(), "Team", Engineers: Array.Empty<EngineerUtilizationEntry>(), OverworkedCount: overworkedCount, AvgLoadPct: avgLoadPct);

    // ── Executive summary ───────────────────────────────────────────────────

    [Fact]
    public void ExecutiveSummary_reports_points_delta_and_workstream_health_mix()
    {
        var workstreams = new[] { Workstream("Alpha", 100, "Healthy"), Workstream("Beta", 40, "AtRisk") };
        var text = WeeklyReportNarrativeGenerator.ExecutiveSummary(
            totalDelivered: 22, previousWeekPoints: 20, workstreams, Array.Empty<BlockerAgingDto>(), Utilization(70, 0));

        text.Should().Contain("22 points").And.Contain("+10%").And.Contain("2 workstreams").And.Contain("1 on track, 1 at risk, 0 delayed");
    }

    [Fact]
    public void ExecutiveSummary_omits_delta_when_there_was_no_previous_week()
    {
        var text = WeeklyReportNarrativeGenerator.ExecutiveSummary(
            totalDelivered: 10, previousWeekPoints: 0, Array.Empty<ProjectHealthDto>(), Array.Empty<BlockerAgingDto>(), Utilization(50, 0));

        text.Should().Contain("10 points").And.NotContain("vs last week");
    }

    [Fact]
    public void ExecutiveSummary_mentions_overworked_count_and_blockers_when_present()
    {
        var text = WeeklyReportNarrativeGenerator.ExecutiveSummary(
            totalDelivered: 5, previousWeekPoints: 5, Array.Empty<ProjectHealthDto>(),
            new[] { Blocker("Stuck task", 9) }, Utilization(95, 2));

        text.Should().Contain("2 engineers flagged as overworked").And.Contain("1 active blocker needs attention");
    }

    [Fact]
    public void ExecutiveSummary_never_claims_zero_load_when_it_is_actually_unmeasurable()
    {
        // AvgLoadPct is null (not 0) when the team has nobody delivery-eligible left — the
        // narrative must say so plainly instead of reporting a fabricated "0%" load.
        var text = WeeklyReportNarrativeGenerator.ExecutiveSummary(
            totalDelivered: 5, previousWeekPoints: 4, Array.Empty<ProjectHealthDto>(),
            Array.Empty<BlockerAgingDto>(), Utilization(null, 0));

        text.Should().NotContain("Team load is at");
        text.Should().Contain("Team load can't be measured");
    }

    // ── Key accomplishments ─────────────────────────────────────────────────

    [Fact]
    public void KeyAccomplishments_lists_each_workstream_with_completion_and_status()
    {
        var text = WeeklyReportNarrativeGenerator.KeyAccomplishments(
            new[] { Workstream("Alpha", 100, "Healthy"), Workstream("Beta", 20, "Critical") });

        text.Should().Contain("- Alpha: 100% complete, on track");
        text.Should().Contain("- Beta: 20% complete, delayed");
    }

    [Fact]
    public void KeyAccomplishments_falls_back_when_no_workstreams()
    {
        WeeklyReportNarrativeGenerator.KeyAccomplishments(Array.Empty<ProjectHealthDto>())
            .Should().Be("No workstreams owned by this team this week.");
    }

    // ── Planned next week ────────────────────────────────────────────────────

    [Fact]
    public void PlannedNextWeek_lists_at_risk_workstreams_and_open_blockers()
    {
        var text = WeeklyReportNarrativeGenerator.PlannedNextWeek(
            new[] { Workstream("Alpha", 100, "Healthy"), Workstream("Beta", 40, "AtRisk") },
            new[] { Blocker("Stuck task", 4) });

        text.Should().Contain("Continue work on Beta");
        text.Should().Contain("Resolve blocker: Stuck task (blocked 4d)");
        text.Should().NotContain("Alpha");
    }

    [Fact]
    public void PlannedNextWeek_falls_back_when_nothing_at_risk_or_blocked()
    {
        var text = WeeklyReportNarrativeGenerator.PlannedNextWeek(
            new[] { Workstream("Alpha", 100, "Healthy") }, Array.Empty<BlockerAgingDto>());

        text.Should().Be("No at-risk workstreams or open blockers; continue current sprint plan.");
    }

    // ── Resourcing notes ─────────────────────────────────────────────────────

    [Fact]
    public void ResourcingNotes_flags_overworked_engineers()
    {
        WeeklyReportNarrativeGenerator.ResourcingNotes(Utilization(92, 1))
            .Should().Contain("92%").And.Contain("1 engineer currently flagged as overworked");
    }

    [Fact]
    public void ResourcingNotes_reports_no_concerns_when_load_is_healthy()
    {
        WeeklyReportNarrativeGenerator.ResourcingNotes(Utilization(60, 0))
            .Should().Be("Team load is at 60%. No resourcing concerns to report this week.");
    }

    [Fact]
    public void ResourcingNotes_never_reports_a_fabricated_zero_percent_load()
    {
        // This is the sharpest version of the bug: a team with nobody baselined used to print
        // "Team load is at 0%. No resourcing concerns to report this week." — a false "no concerns"
        // statement dressed up with a fabricated number, and a team lead could sign that off as-is.
        var text = WeeklyReportNarrativeGenerator.ResourcingNotes(Utilization(null, 0));

        text.Should().NotContain("0%");
        text.Should().Contain("can't be measured");
    }

    // ── Why a workstream isn't on track ─────────────────────────────────────

    [Fact]
    public void KeyAccomplishments_and_PlannedNextWeek_say_why_a_workstream_is_not_on_track()
    {
        var reasons = new[]
        {
            new HealthReasonDto("overdue", 2, "2 overdue tasks", []),
            new HealthReasonDto("blocked_long", 1, "1 blocked 5+ working days (oldest 7)", []),
        };
        var beta = Workstream("Beta", 40, "Critical") with { Reasons = reasons };
        var healthy = Workstream("Alpha", 100, "Healthy");

        WeeklyReportNarrativeGenerator.KeyAccomplishments(new[] { healthy, beta })
            .Should().Contain("- Alpha: 100% complete, on track")
            .And.NotContain("- Alpha: 100% complete, on track —")
            .And.Contain("- Beta: 40% complete, delayed — 2 overdue tasks; 1 blocked 5+ working days (oldest 7)");
        WeeklyReportNarrativeGenerator.PlannedNextWeek(new[] { healthy, beta }, Array.Empty<BlockerAgingDto>())
            .Should().Contain("currently delayed — 2 overdue tasks; 1 blocked 5+ working days (oldest 7)");
    }
}
