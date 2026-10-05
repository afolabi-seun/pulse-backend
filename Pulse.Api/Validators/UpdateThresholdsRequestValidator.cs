using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpdateThresholdsRequestValidator : AbstractValidator<ThresholdsController.UpdateThresholdsRequest>
{
    public UpdateThresholdsRequestValidator()
    {
        RuleFor(x => x.LoadVsBaselineRatio)
            .GreaterThan(0).WithMessage("Load vs baseline ratio must be positive.")
            .When(x => x.LoadVsBaselineRatio.HasValue);

        RuleFor(x => x.MaxConcurrentTasks)
            .GreaterThan(0).WithMessage("Max concurrent tasks must be positive.")
            .When(x => x.MaxConcurrentTasks.HasValue);

        RuleFor(x => x.StaleCycleMultiplier)
            .GreaterThan(0).WithMessage("Stale cycle multiplier must be positive.")
            .When(x => x.StaleCycleMultiplier.HasValue);

        RuleFor(x => x.SignalsRequiredToFlag)
            .InclusiveBetween(1, 3).WithMessage("Signals required to flag must be between 1 and 3.")
            .When(x => x.SignalsRequiredToFlag.HasValue);

        RuleFor(x => x.EscalationT3Days)
            .GreaterThan(0).WithMessage("T-3 days threshold must be positive.")
            .When(x => x.EscalationT3Days.HasValue);

        RuleFor(x => x.EscalationT3ElapsedPct)
            .InclusiveBetween(0, 1).WithMessage("T-3 elapsed percentage must be between 0 and 1.")
            .When(x => x.EscalationT3ElapsedPct.HasValue);

        RuleFor(x => x.EscalationT1Days)
            .GreaterThan(0).WithMessage("T-1 days threshold must be positive.")
            .When(x => x.EscalationT1Days.HasValue);

        RuleFor(x => x.EscalationT1ElapsedPct)
            .InclusiveBetween(0, 1).WithMessage("T-1 elapsed percentage must be between 0 and 1.")
            .When(x => x.EscalationT1ElapsedPct.HasValue);

        RuleFor(x => x.EscalationT3MinHours)
            .GreaterThanOrEqualTo(0).WithMessage("T-3 minimum hours must be zero or positive.")
            .When(x => x.EscalationT3MinHours.HasValue);

        RuleFor(x => x.EscalationT1MinHours)
            .GreaterThanOrEqualTo(0).WithMessage("T-1 minimum hours must be zero or positive.")
            .When(x => x.EscalationT1MinHours.HasValue);

        RuleFor(x => x.QaLeadTimeDays)
            .GreaterThan(0).WithMessage("QA lead time must be positive.")
            .When(x => x.QaLeadTimeDays.HasValue);

        RuleFor(x => x.PointScale)
            .Must(ps => ps == null || ps.Count >= 1)
                .WithMessage("Point scale must have at least one entry.")
            .Must(ps => ps == null || ps.All(e => e.Value > 0))
                .WithMessage("All point values must be positive integers.")
            .Must(ps => ps == null || ps.Select(e => e.Value).Distinct().Count() == ps.Count)
                .WithMessage("Point values must be unique.")
            .When(x => x.PointScale != null);

        RuleFor(x => x.PriorityScale)
            .Must(ps => ps == null || ps.Count >= 1)
                .WithMessage("Priority scale must have at least one entry.")
            .Must(ps => ps == null || ps.All(e => e.Value is >= 1 and <= 5))
                .WithMessage("Priority values must be between 1 and 5 — that's the range a task's priority can actually be set to.")
            .Must(ps => ps == null || ps.Select(e => e.Value).Distinct().Count() == ps.Count)
                .WithMessage("Priority values must be unique.")
            .When(x => x.PriorityScale != null);
    }
}
