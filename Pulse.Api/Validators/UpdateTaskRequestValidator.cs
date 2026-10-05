using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateTaskRequestValidator : AbstractValidator<TasksController.UpdateTaskRequest>
{
    public UpdateTaskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200).When(x => x.Title is not null);
        RuleFor(x => x.Points).InclusiveBetween(1, 13).When(x => x.Points.HasValue);
        RuleFor(x => x.Priority).InclusiveBetween(1, 5).When(x => x.Priority.HasValue);
        RuleFor(x => x.DueDateChangeReason).MaximumLength(500).When(x => x.DueDateChangeReason is not null);
        RuleFor(x => x.PointsChangeReason).MaximumLength(500).When(x => x.PointsChangeReason is not null);
    }
}
