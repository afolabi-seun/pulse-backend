using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class InitiatePasswordResetRequestValidator : AbstractValidator<AuthController.InitiatePasswordResetRequest>
{
    public InitiatePasswordResetRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}
