using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class CreateTaskRequestValidator : AbstractValidator<TasksController.CreateTaskRequest>
{
    public CreateTaskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Points).InclusiveBetween(1, 13).When(x => x.Points.HasValue);
        RuleFor(x => x.ProjectId).NotEmpty().When(x => !x.Personal);
        RuleFor(x => x.Priority).InclusiveBetween(1, 5).When(x => x.Priority.HasValue);
    }
}
