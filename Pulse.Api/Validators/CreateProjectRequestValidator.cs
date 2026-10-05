using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateProjectRequestValidator : AbstractValidator<ProjectsController.CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
        // A project with no owner team is invisible to every team-scoped report (Weekly Report,
        // sprint/task team filters) — only the unfiltered org-wide PMO report would ever show it.
        // Require an explicit team rather than silently falling back to the creator's own team
        // (which is null for department heads, producing an orphaned project).
        RuleFor(x => x.OwnerTeamId).NotEmpty().WithMessage("An owner team is required.");
    }
}
