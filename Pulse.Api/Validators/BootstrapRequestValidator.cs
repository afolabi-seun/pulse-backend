using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class BootstrapRequestValidator : AbstractValidator<AuthController.BootstrapRequest>
{
    public BootstrapRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MustBeStrongPassword();
    }
}
