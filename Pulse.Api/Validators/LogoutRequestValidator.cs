using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class LogoutRequestValidator : AbstractValidator<AuthController.LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
