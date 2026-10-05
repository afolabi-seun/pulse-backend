using Pulse.Api.Controllers;
using FluentValidation;

namespace Pulse.Api.Validators;

public class UpsertDepartmentThresholdRequestValidator : AbstractValidator<ThresholdsController.UpsertDepartmentThresholdRequest>
{
    public UpsertDepartmentThresholdRequestValidator()
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
    }
}
