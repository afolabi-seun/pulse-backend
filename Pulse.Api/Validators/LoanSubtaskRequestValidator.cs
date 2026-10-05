using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class LoanSubtaskRequestValidator : AbstractValidator<TasksController.LoanSubtaskRequest>
{
    public LoanSubtaskRequestValidator()
    {
        RuleFor(x => x.TargetEngineerId).NotEmpty()
            .WithMessage("A target engineer must be specified.");
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason is not null);
    }
}
