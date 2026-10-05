using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class BulkReassignRequestValidator : AbstractValidator<TasksController.BulkReassignRequest>
{
    public BulkReassignRequestValidator()
    {
        RuleFor(x => x.TaskIds)
            .NotEmpty().WithMessage("At least one task ID is required.");

        RuleFor(x => x.TargetEngineerId).NotEmpty();
    }
}
