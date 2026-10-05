using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class RefreshTokenRequestValidator : AbstractValidator<AuthController.RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
