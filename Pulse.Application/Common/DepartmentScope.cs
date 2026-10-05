using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Teams;

namespace Pulse.Application.Common;

/// <summary>
/// Resolves the set of engineer ids in an actor's department — the unit of "departmentalized"
/// visibility used by project-follow scoping (and reusable by other department-scoped reads).
/// </summary>
public static class DepartmentScope
{
    /// <summary>
    /// Returns the ids of all engineers in the actor's department, or <c>null</c> when the actor is
    /// unscoped (no team, or a team with no department set) — callers treat null as "see all".
    /// </summary>
    public static async Task<HashSet<Guid>?> EngineerIdsAsync(
        Guid actorId, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var actor = await engineers.GetByIdAsync(actorId, ct);
        if (actor?.TeamId is not Guid teamId) return null;

        var team = await teams.GetByIdAsync(teamId, ct);
        if (team?.Department is not string department) return null;

        return await EngineerIdsForDepartmentAsync(department, engineers, teams, ct);
    }

    /// <summary>The ids of all engineers on any team in the given department.</summary>
    public static async Task<HashSet<Guid>> EngineerIdsForDepartmentAsync(
        string department, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var deptTeamIds = (await teams.ListAllAsync(ct))
            .Where(t => t.Department == department)
            .Select(t => t.Id)
            .ToHashSet();

        var all = await engineers.ListAllAsync(ct);
        return all
            .Where(e => e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
            .Select(e => e.Id)
            .ToHashSet();
    }

    // Same role set CapabilityRegistry repeats per department-head-eligible capability (e.g.
    // TeamLeadOrAbove) — kept here too since this is a data-driven lookup, not a role-list gate.
    private static readonly HashSet<string> HeadRoles = new()
    {
        Domain.Engineers.Roles.HeadOfRnD, Domain.Engineers.Roles.HeadOfProduct, Domain.Engineers.Roles.HeadOfDesign,
        Domain.Engineers.Roles.HeadOfPmo, Domain.Engineers.Roles.HeadOfFunctional,
        Domain.Engineers.Roles.HeadOfCoreBanking, Domain.Engineers.Roles.HeadOfInfraDevOps,
    };

    /// <summary>
    /// The active engineer(s) holding a "Head of X" role whose own team's Department matches the
    /// given engineer's team's Department — i.e., that engineer's own department head(s). Empty
    /// when the engineer has no team, their team has no Department set, or no head's team matches
    /// it (e.g. HeadOfPmo/HeadOfProduct are deliberately unscoped by role and carry no team of
    /// their own — see DemoSeeder — so they never resolve here; callers need their own fallback
    /// for that case, same as DepartmentScope.EngineerIdsAsync's null-department "see all").
    /// </summary>
    public static async Task<IReadOnlyList<Domain.Engineers.Engineer>> GetDepartmentHeadsAsync(
        Guid engineerId, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var engineer = await engineers.GetByIdAsync(engineerId, ct);
        if (engineer?.TeamId is not Guid teamId) return [];

        var team = await teams.GetByIdAsync(teamId, ct);
        if (team?.Department is not string department) return [];

        var deptTeamIds = (await teams.ListAllAsync(ct))
            .Where(t => t.Department == department)
            .Select(t => t.Id)
            .ToHashSet();

        var all = await engineers.ListActiveAsync(ct);
        return all
            .Where(e => HeadRoles.Contains(e.Role) && e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
            .ToList();
    }

    /// <summary>The Team Lead of the given engineer's own team, or null if they have no team,
    /// their team has no lead assigned, or they <i>are</i> that lead (can't approve your own
    /// estimate) — used by EstimationApproval's Team-Lead-first-tier resolution.</summary>
    public static async Task<Domain.Engineers.Engineer?> GetOwnTeamLeadAsync(
        Guid engineerId, IEngineerRepository engineers, ITeamRepository teams, CancellationToken ct)
    {
        var engineer = await engineers.GetByIdAsync(engineerId, ct);
        if (engineer?.TeamId is not Guid teamId) return null;

        var team = await teams.GetByIdAsync(teamId, ct);
        if (team?.TeamLeadId is not Guid leadId || leadId == engineerId) return null;

        return await engineers.GetByIdAsync(leadId, ct);
    }

    /// <summary>
    /// The team this actor is the designated lead of, or null if they don't lead one.
    /// A team lead's own Engineer.TeamId isn't a reliable signal of which team they lead — creating
    /// or updating a team never assigns its lead as one of its own members — so Team.TeamLeadId is
    /// the source of truth (also used by LoanTaskCommand).
    /// </summary>
    public static async Task<Team?> GetLedTeamAsync(Guid actorId, ITeamRepository teams, CancellationToken ct) =>
        (await teams.ListAllAsync(ct)).FirstOrDefault(t => t.TeamLeadId == actorId);
}
