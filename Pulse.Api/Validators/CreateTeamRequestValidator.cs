using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateTeamRequestValidator : AbstractValidator<TeamsController.CreateTeamRequest>
{
    public CreateTeamRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
