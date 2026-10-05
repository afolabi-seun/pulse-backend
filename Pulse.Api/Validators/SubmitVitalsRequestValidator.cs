using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class SubmitVitalsRequestValidator : AbstractValidator<VitalsController.SubmitVitalsRequest>
{
    public SubmitVitalsRequestValidator()
    {
        RuleFor(x => x.Score).InclusiveBetween(1, 5)
            .WithMessage("Score must be between 1 and 5.");

        RuleFor(x => x.Comment)
            .MaximumLength(1000)
            .When(x => x.Comment is not null);
    }
}
