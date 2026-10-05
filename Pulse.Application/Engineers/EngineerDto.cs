using Pulse.Domain.Engineers;

namespace Pulse.Application.Engineers;

/// <summary>PM-facing view of an engineer — excludes sensitive auth fields.</summary>
public record EngineerDto(
    Guid Id,
    string Name,
    string Email,
    string Role,
    bool IsActive,
    int BaselinePoints,
    int BaselineCycleDays,
    string? Team,
    Guid? TeamId,
    bool IsQa = false,
    string? Discipline = null,
    int ActiveTasks = 0,
    int TotalPoints = 0,
    bool IsOverworked = false,
    // The part of TotalPoints due within the engineer's baseline cycle (plus overdue/undated) — what the overwork
    // signal compares with the baseline. Only set where load is assessed (the Engineers page), not on the roster.
    int CyclePoints = 0)
{
    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];

    public static EngineerDto From(Engineer e) => new(
        e.Id, e.Name, e.Email, e.Role, e.IsActive,
        e.BaselinePoints, e.BaselineCycleDays, e.Team, e.TeamId,
        e.IsQa, e.Discipline.HasValue ? Camel(e.Discipline.Value.ToString()) : null);

    /// <summary>Roster view with raw workload counts. Load is deliberately not assessed here — the roster feeds
    /// assign/reassign pickers, which must stay cheap — so IsOverworked and CyclePoints are left unset. Use
    /// <see cref="FromAssessedWorkload"/> where "overworked" is shown.</summary>
    public static EngineerDto FromWithWorkload(Engineer e, int activeTasks, int totalPoints) => new(
        e.Id, e.Name, e.Email, e.Role, e.IsActive,
        e.BaselinePoints, e.BaselineCycleDays, e.Team, e.TeamId,
        e.IsQa, e.Discipline.HasValue ? Camel(e.Discipline.Value.ToString()) : null,
        activeTasks, totalPoints);

    /// <summary>Workload with the overwork signal's verdict and cycle load (see EngineerWorkloadAssessor).</summary>
    public static EngineerDto FromAssessedWorkload(Engineer e, int activeTasks, int totalPoints, int cyclePoints, bool isOverworked) => new(
        e.Id, e.Name, e.Email, e.Role, e.IsActive,
        e.BaselinePoints, e.BaselineCycleDays, e.Team, e.TeamId,
        e.IsQa, e.Discipline.HasValue ? Camel(e.Discipline.Value.ToString()) : null,
        activeTasks, totalPoints, isOverworked, cyclePoints);
}
