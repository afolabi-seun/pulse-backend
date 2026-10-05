using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class BulkCreateTasksRequestValidator : AbstractValidator<TasksController.BulkCreateTasksRequest>
{
    public BulkCreateTasksRequestValidator()
    {
        RuleFor(x => x.ProjectId).NotEmpty();

        RuleFor(x => x.Tasks)
            .NotEmpty().WithMessage("Tasks list must contain at least one item.")
            .Must(t => t.Count <= 200).WithMessage("Cannot bulk-create more than 200 tasks at once.");

        RuleForEach(x => x.Tasks).SetValidator(new BulkTaskItemValidator());
    }

    private sealed class BulkTaskItemValidator : AbstractValidator<TasksController.BulkCreateTaskItem>
    {
        public BulkTaskItemValidator()
        {
            RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Points).InclusiveBetween(1, 13);
        }
    }
}
