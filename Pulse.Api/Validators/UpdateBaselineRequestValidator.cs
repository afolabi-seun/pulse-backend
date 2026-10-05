using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateBaselineRequestValidator : AbstractValidator<EngineersController.UpdateBaselineRequest>
{
    public UpdateBaselineRequestValidator()
    {
        RuleFor(x => x.BaselinePoints).GreaterThan(0);
        RuleFor(x => x.BaselineCycleDays).GreaterThan(0);
    }
}
