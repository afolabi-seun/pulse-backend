using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class LoginRequestValidator : AbstractValidator<AuthController.LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
