using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

/// <summary>Pure selection logic for "who should review this by default" — shared between
/// SendToQaHandler's actual auto-assign (when the caller submits no explicit reviewer) and
/// GetQaSendCandidatesQuery's "recommended" hint (so the picker's suggested default and what
/// would happen if you accepted it can never drift apart). Preference order: (1) discipline match
/// on the project, (2) discipline match anywhere in the org, (3) any active QA engineer already on
/// the project, (4) any active QA engineer anywhere. Tier 4 matters even when the task has no
/// discipline — a common, expected case — so a discipline-less task never ends up with a narrower
/// search than one that has it.</summary>
public static class QaAutoAssignment
{
    public static Guid? Recommend(
        IReadOnlyList<Engineer> qaEngineersOnProject,
        IReadOnlyList<Engineer> allActiveQaEngineers,
        Discipline? discipline)
    {
        if (discipline.HasValue)
        {
            var onProjectMatch = qaEngineersOnProject.FirstOrDefault(e => e.Discipline == discipline);
            if (onProjectMatch is not null) return onProjectMatch.Id;

            var anywhereMatch = allActiveQaEngineers.Where(e => e.Discipline == discipline).OrderBy(e => e.Name).FirstOrDefault();
            if (anywhereMatch is not null) return anywhereMatch.Id;
        }

        var onProjectAny = qaEngineersOnProject.FirstOrDefault();
        if (onProjectAny is not null) return onProjectAny.Id;

        return allActiveQaEngineers.OrderBy(e => e.Name).FirstOrDefault()?.Id;
    }
}
