using Pulse.Api.Controllers;
using Pulse.Domain.TimeEntries;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateTimeEntryRequestValidator : AbstractValidator<TimeEntriesController.UpdateTimeEntryRequest>
{
    public UpdateTimeEntryRequestValidator()
    {
        RuleFor(x => x.Date)
            .NotEqual(DateOnly.MinValue)
            .WithMessage("Date is required.")
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Cannot log time for a future date.");

        RuleFor(x => x.Category)
            .Must(c => Enum.TryParse<TimeEntryCategory>(c, ignoreCase: true, out _))
            .WithMessage("Category must be one of: Task, Meeting, Admin, Leave, Other.");

        RuleFor(x => x.Hours).GreaterThan(0).LessThanOrEqualTo(24);
        RuleFor(x => x.Note).MaximumLength(1000).When(x => x.Note is not null);
    }
}
