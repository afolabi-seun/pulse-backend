using System.Text.Json;

namespace Pulse.Application.Overwork;

public class PointScaleEntry
{
    public int Value { get; set; }
    public string Label { get; set; } = string.Empty;
    public string TimeGuide { get; set; } = string.Empty;
}

public class PriorityScaleEntry
{
    public int Value { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Criteria { get; set; } = string.Empty;
}

public class OverworkThresholds
{
    // Overwork detection
    public double LoadVsBaselineRatio { get; set; } = 1.3;
    public int MaxConcurrentTasks { get; set; } = 3;
    public double StaleCycleMultiplier { get; set; } = 1.5;
    public int SignalsRequiredToFlag { get; set; } = 2;

    // Escalation timing — dual-trigger model (see spec §5.6, §14)
    // T-3 fires at the LATER of: T3ElapsedPct% elapsed OR T3Days days remaining
    public double EscalationT3Days { get; set; } = 3;
    public double EscalationT3ElapsedPct { get; set; } = 0.60;
    public double EscalationT1Days { get; set; } = 1;
    public double EscalationT1ElapsedPct { get; set; } = 0.85;

    // Minimum remaining time floor — don't fire T-3/T-1 if barely any time is left
    // (avoids spamming a T-3 alert 90 minutes before a task is due on a short-duration task)
    public double EscalationT3MinHours { get; set; } = 2.0;
    public double EscalationT1MinHours { get; set; } = 1.0;

    // Grace window after a task's clock restarts (e.g. reactivated from a QA rejection) before
    // "Overdue" can fire, even if the due date itself is already behind it — gives the assignee a
    // beat before the full PM/dept-head broadcast goes out for work they just got back.
    public double EscalationReactivationGraceHours { get; set; } = 24.0;

    // How long a pending Planning Poker estimate sits with just the assignee's Team Lead before
    // the department head also gets visibility — the Team Lead keeps the ability to act after
    // this, the head just stops having to wait on them. See EstimateApprovalEscalationScanner.
    public double EstimateApprovalEscalationHours { get; set; } = 24.0;

    // QA hand-off — business days given to review, starting the day a task is sent to QA
    // (see BusinessDays.Add; independent of the original task's own due date)
    public int QaLeadTimeDays { get; set; } = 2;

    // Project health (see ProjectHealthCalculator) — a blocker that has sat this many working days makes
    // its project "Critical" rather than merely "At Risk"...
    public int BlockerCriticalBusinessDays { get; set; } = 5;

    // ...and overdue work is judged by how late it is, not just that it is late: a task up to
    // OverdueGraceBusinessDays working days past due is a "just missed it" (At Risk at worst); one
    // OverdueCriticalBusinessDays or more working days late makes its project Critical, as do
    // CriticalOverdueTasks tasks that are past the grace.
    public int OverdueGraceBusinessDays { get; set; } = 2;
    public int OverdueCriticalBusinessDays { get; set; } = 5;
    public int CriticalOverdueTasks { get; set; } = 3;

    // Configurable story-point scale with per-value labels and time guides
    public PointScaleEntry[] PointScale { get; set; } =
    [
        new() { Value = 1,  Label = "Trivial",    TimeGuide = "A few hours" },
        new() { Value = 2,  Label = "Simple",     TimeGuide = "~Half a day" },
        new() { Value = 3,  Label = "Small",      TimeGuide = "~1 day"      },
        new() { Value = 5,  Label = "Medium",     TimeGuide = "2–3 days"    },
        new() { Value = 8,  Label = "Large",      TimeGuide = "~1 week"     },
        new() { Value = 13, Label = "Very large", TimeGuide = "1–2 weeks"   },
        new() { Value = 21, Label = "Huge",       TimeGuide = "—"           },
    ];

    // Configurable priority scale with per-value labels and triage criteria — describes what P1..P5
    // mean org-wide, so it's not department-overridable (see DepartmentThresholdResolver).
    // 1 (lowest) to 5 (highest), matching the existing PRIORITY_COLORS ramp on the frontend
    // (taskStatus.ts) which escalates from muted at P1 to red at P5.
    public PriorityScaleEntry[] PriorityScale { get; set; } =
    [
        new() { Value = 1, Label = "Backlog",  Criteria = "No urgency, safe to slip indefinitely" },
        new() { Value = 2, Label = "Low",      Criteria = "Should land this cycle if time allows" },
        new() { Value = 3, Label = "Normal",   Criteria = "Standard sprint work, no external dependency" },
        new() { Value = 4, Label = "High",     Criteria = "Committed for this cycle, on the critical path" },
        new() { Value = 5, Label = "Critical", Criteria = "Blocking another team, a customer, or production" },
    ];

    /// <summary>Overwrites defaults with whatever an organization has saved in threshold_settings.
    /// Unknown keys and unparseable values are ignored, leaving that threshold at its default.</summary>
    public void Apply(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("LoadVsBaselineRatio", out var v1) && double.TryParse(v1, out var ratio))
            LoadVsBaselineRatio = ratio;
        if (settings.TryGetValue("MaxConcurrentTasks", out var v2) && int.TryParse(v2, out var concurrent))
            MaxConcurrentTasks = concurrent;
        if (settings.TryGetValue("StaleCycleMultiplier", out var v3) && double.TryParse(v3, out var stale))
            StaleCycleMultiplier = stale;
        if (settings.TryGetValue("SignalsRequiredToFlag", out var v4) && int.TryParse(v4, out var signals))
            SignalsRequiredToFlag = signals;
        if (settings.TryGetValue("EscalationT3Days", out var v5) && double.TryParse(v5, out var t3Days))
            EscalationT3Days = t3Days;
        if (settings.TryGetValue("EscalationT3ElapsedPct", out var v6) && double.TryParse(v6, out var t3Pct))
            EscalationT3ElapsedPct = t3Pct;
        if (settings.TryGetValue("EscalationT1Days", out var v7) && double.TryParse(v7, out var t1Days))
            EscalationT1Days = t1Days;
        if (settings.TryGetValue("EscalationT1ElapsedPct", out var v8) && double.TryParse(v8, out var t1Pct))
            EscalationT1ElapsedPct = t1Pct;
        if (settings.TryGetValue("EscalationT3MinHours", out var v9) && double.TryParse(v9, out var t3MinH))
            EscalationT3MinHours = t3MinH;
        if (settings.TryGetValue("EscalationT1MinHours", out var v10) && double.TryParse(v10, out var t1MinH))
            EscalationT1MinHours = t1MinH;
        if (settings.TryGetValue("QaLeadTimeDays", out var v11) && int.TryParse(v11, out var qaLead))
            QaLeadTimeDays = qaLead;
        if (settings.TryGetValue("PointScale", out var psJson) && !string.IsNullOrEmpty(psJson))
        {
            var entries = JsonSerializer.Deserialize<PointScaleEntry[]>(psJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entries is { Length: > 0 })
                PointScale = entries;
        }
        if (settings.TryGetValue("PriorityScale", out var prJson) && !string.IsNullOrEmpty(prJson))
        {
            var entries = JsonSerializer.Deserialize<PriorityScaleEntry[]>(prJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entries is { Length: > 0 })
                PriorityScale = entries;
        }
    }
}
