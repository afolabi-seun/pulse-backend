using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateOverrideRequestValidator : AbstractValidator<EngineersController.CreateOverrideRequest>
{
    public CreateOverrideRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);

        RuleFor(x => x.ExpiresAt)
            .GreaterThan(DateTime.UtcNow).WithMessage("Expiry date must be in the future.")
            .When(x => x.ExpiresAt.HasValue);
    }
}
