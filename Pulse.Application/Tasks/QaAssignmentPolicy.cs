using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

public record QaAssignmentCheckResult(bool IsAllowed, string? ErrorCode, string? ErrorMessage)
{
    public static readonly QaAssignmentCheckResult Ok = new(true, null, null);
    public static QaAssignmentCheckResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>
/// Shared validation for assigning/reassigning a QA review task (ParentTaskId set), used by every
/// manual-assignment path — UpdateTaskHandler, BulkReassignCommand, LoanTaskCommand.
///
/// A QA task always requires an IsQa engineer. When the *original* task has no discipline set,
/// there's no natural QA owner to route to — that ambiguity is narrowed by restricting who may make
/// the call (Head of Product, Head of PMO, or Head of Functional only) and which department the
/// target engineer must be in (Product or Functional), rather than leaving it open to any editor
/// and any QA engineer the way a discipline-tagged task already is.
/// </summary>
public static class QaAssignmentPolicy
{
    public static readonly IReadOnlySet<string> NoDisciplineAssignerRoles = new HashSet<string>
    {
        Roles.HeadOfProduct, Roles.HeadOfPmo, Roles.HeadOfFunctional,
    };

    public static readonly IReadOnlySet<string> NoDisciplineDepartments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Product", "Functional",
    };

    public static async Task<QaAssignmentCheckResult> ValidateAsync(
        PulseTask qaTask, Engineer targetEngineer, string actorRole,
        ITaskRepository tasks, ITeamRepository teams, CancellationToken ct)
    {
        if (!qaTask.ParentTaskId.HasValue)
            return QaAssignmentCheckResult.Ok;

        if (!targetEngineer.IsQa)
            return QaAssignmentCheckResult.Fail("BUSINESS_RULE_VIOLATION",
                "This is a QA review task — it can only be assigned to a QA engineer.");

        var parent = await tasks.GetByIdAsync(qaTask.ParentTaskId.Value, ct);
        if (parent?.Discipline is not null)
            return QaAssignmentCheckResult.Ok;

        if (!NoDisciplineAssignerRoles.Contains(actorRole))
            return QaAssignmentCheckResult.Fail("FORBIDDEN",
                "Only Head of Product, Head of PMO, or Head of Functional may assign a QA task whose original task has no discipline set.");

        var team = targetEngineer.TeamId.HasValue
            ? await teams.GetByIdAsync(targetEngineer.TeamId.Value, ct)
            : null;

        if (team?.Department is null || !NoDisciplineDepartments.Contains(team.Department))
            return QaAssignmentCheckResult.Fail("BUSINESS_RULE_VIOLATION",
                "A QA task whose original task has no discipline can only be assigned to an engineer in the Product or Functional department.");

        return QaAssignmentCheckResult.Ok;
    }
}
