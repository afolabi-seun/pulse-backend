using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateSprintRequestValidator : AbstractValidator<SprintsController.UpdateSprintRequest>
{
    public UpdateSprintRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Goal).MaximumLength(2000).When(x => x.Goal is not null);

        RuleFor(x => x.DueDateChangeReason).MaximumLength(500).When(x => x.DueDateChangeReason is not null);

        // Only validate date ordering when both dates are supplied together.
        When(x => x.StartDate.HasValue && x.EndDate.HasValue, () =>
        {
            RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate)
                .WithMessage("End date must be after start date.");
        });
    }
}
