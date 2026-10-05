using Pulse.Application.Overwork;
using FluentAssertions;

namespace Pulse.UnitTests.Overwork;

public class DepartmentThresholdResolverTests
{
    private static OverworkThresholds GlobalDefaults() => new()
    {
        LoadVsBaselineRatio = 1.3,
        MaxConcurrentTasks = 3,
        StaleCycleMultiplier = 1.5,
        SignalsRequiredToFlag = 2,
    };

    [Fact]
    public void Returns_global_unchanged_when_no_override()
    {
        var global = GlobalDefaults();

        var effective = DepartmentThresholdResolver.Resolve(global, null);

        effective.Should().BeSameAs(global);
    }

    [Fact]
    public void Override_field_wins_when_set()
    {
        var global = GlobalDefaults();
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", LoadVsBaselineRatio = 2.0 };

        var effective = DepartmentThresholdResolver.Resolve(global, deptOverride);

        effective.LoadVsBaselineRatio.Should().Be(2.0);
    }

    [Fact]
    public void Unset_override_fields_fall_back_to_global_per_field()
    {
        var global = GlobalDefaults();
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", MaxConcurrentTasks = 5 };

        var effective = DepartmentThresholdResolver.Resolve(global, deptOverride);

        effective.MaxConcurrentTasks.Should().Be(5, "explicitly overridden");
        effective.LoadVsBaselineRatio.Should().Be(global.LoadVsBaselineRatio, "not overridden — falls back to global");
        effective.StaleCycleMultiplier.Should().Be(global.StaleCycleMultiplier, "not overridden — falls back to global");
        effective.SignalsRequiredToFlag.Should().Be(global.SignalsRequiredToFlag, "not overridden — falls back to global");
    }

    [Fact]
    public void Resolving_never_mutates_the_global_instance()
    {
        var global = GlobalDefaults();
        var originalRatio = global.LoadVsBaselineRatio;
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", LoadVsBaselineRatio = 99 };

        DepartmentThresholdResolver.Resolve(global, deptOverride);

        global.LoadVsBaselineRatio.Should().Be(originalRatio, "the shared global singleton must not be mutated by a per-call resolve");
    }

    [Fact]
    public void Non_overridable_fields_always_carry_through_from_global()
    {
        var global = GlobalDefaults();
        global.QaLeadTimeDays = 7;
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", LoadVsBaselineRatio = 2.0 };

        var effective = DepartmentThresholdResolver.Resolve(global, deptOverride);

        effective.QaLeadTimeDays.Should().Be(7);
        effective.PointScale.Should().BeSameAs(global.PointScale);
        effective.PriorityScale.Should().BeSameAs(global.PriorityScale);
    }
}
