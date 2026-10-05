using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateProjectRequestValidator : AbstractValidator<ProjectsController.UpdateProjectRequest>
{
    public UpdateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description is not null);
    }
}
