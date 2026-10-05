using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class PreviewAssignRequestValidator : AbstractValidator<TasksController.PreviewAssignRequest>
{
    public PreviewAssignRequestValidator()
    {
        RuleFor(x => x.TargetEngineerId).NotEmpty();
    }
}
