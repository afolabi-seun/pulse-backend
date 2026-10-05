using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class SubmitFeedbackRequestValidator : AbstractValidator<FeedbackController.SubmitFeedbackRequest>
{
    public SubmitFeedbackRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.TaskId).NotEqual(Guid.Empty).When(x => x.TaskId.HasValue);
    }
}
