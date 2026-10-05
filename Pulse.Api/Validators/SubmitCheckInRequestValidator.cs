using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class SubmitCheckInRequestValidator : AbstractValidator<CheckInsController.SubmitCheckInRequest>
{
    public SubmitCheckInRequestValidator()
    {
        RuleFor(x => x.Date)
            .NotEqual(DateOnly.MinValue)
            .WithMessage("Date is required.")
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Cannot submit a check-in for a future date.");

        RuleFor(x => x.Completed).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.PlannedNext).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Blockers).MaximumLength(2000).When(x => x.Blockers is not null);
    }
}
