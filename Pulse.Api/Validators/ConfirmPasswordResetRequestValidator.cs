using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class ConfirmPasswordResetRequestValidator : AbstractValidator<AuthController.ConfirmPasswordResetRequest>
{
    public ConfirmPasswordResetRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MustBeStrongPassword();
    }
}
