using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class FlagBlockerRequestValidator : AbstractValidator<TasksController.FlagBlockerRequest>
{
    public FlagBlockerRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
