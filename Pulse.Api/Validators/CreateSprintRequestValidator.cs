using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateSprintRequestValidator : AbstractValidator<SprintsController.CreateSprintRequest>
{
    public CreateSprintRequestValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Goal).MaximumLength(2000).When(x => x.Goal is not null);
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate)
            .WithMessage("End date must be after start date.");
    }
}
