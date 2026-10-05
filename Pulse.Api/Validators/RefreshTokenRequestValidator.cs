using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class RefreshTokenRequestValidator : AbstractValidator<AuthController.RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        // Optional: leaving it out means "use the cookie". A token that is sent must not be blank.
        RuleFor(x => x.RefreshToken).NotEmpty().When(x => x.RefreshToken is not null);
    }
}
