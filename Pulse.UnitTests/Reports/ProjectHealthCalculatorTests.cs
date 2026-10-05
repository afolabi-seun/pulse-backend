using Pulse.Application.Common;
using Pulse.Application.Overwork;
using Pulse.Application.Reports;
using FluentAssertions;

namespace Pulse.UnitTests.Reports;

public class ProjectHealthCalculatorTests
{
    // Defaults: blocker Critical at 5 working days; due-soon window 3 days; overdue grace 2 working days;
    // a task is Critical-late at 5 working days; Critical at 3 tasks late past the grace.
    private readonly OverworkThresholds _thresholds = new();

    private static HealthTaskRef Task(string title, int days, string? assignee = "Alice") =>
        new(Guid.NewGuid(), $"APP-{title.Length}", title, assignee, days);

    private ProjectHealthAssessment Assess(
        HealthTaskRef[]? overdue = null, HealthTaskRef[]? dueSoon = null, HealthTaskRef[]? blocked = null) =>
        ProjectHealthCalculator.Assess(overdue ?? [], dueSoon ?? [], blocked ?? [], _thresholds);

    [Fact]
    public void A_project_with_nothing_wrong_is_healthy_with_no_reasons_or_steps()
    {
        var result = Assess();

        result.Health.Should().Be("Healthy");
        result.Reasons.Should().BeEmpty();
        result.NextSteps.Should().BeEmpty();
    }

    [Fact]
    public void Something_due_soon_makes_it_at_risk_not_critical()
    {
        var result = Assess(dueSoon: [Task("Ship login", 2)]);

        result.Health.Should().Be("AtRisk");
        result.Reasons.Should().ContainSingle(r => r.Kind == "due_soon" && r.Count == 1);
    }

    [Fact]
    public void A_recently_blocked_task_makes_it_at_risk()
    {
        var result = Assess(blocked: [Task("Waiting on vendor", 2)]);

        result.Health.Should().Be("AtRisk");
        result.Reasons.Should().ContainSingle(r => r.Kind == "blocked");
    }

    [Theory]
    [InlineData(1, "AtRisk")]   // just missed it
    [InlineData(2, "AtRisk")]   // still inside the 2-day grace
    [InlineData(3, "AtRisk")]   // past the grace, but one task is not enough
    [InlineData(4, "AtRisk")]
    [InlineData(5, "Critical")] // late a full working week
    [InlineData(12, "Critical")]
    public void A_single_late_task_is_critical_only_once_it_has_been_late_long_enough(int workingDaysLate, string expected)
    {
        var result = Assess(overdue: [Task("Late thing", workingDaysLate)]);

        result.Health.Should().Be(expected);
        result.Reasons.Should().ContainSingle(r => r.Kind == "overdue");
    }

    [Fact]
    public void The_overdue_reason_says_how_late_the_oldest_is_in_working_days()
    {
        var one = Assess(overdue: [Task("a", 1)]).Reasons.Single();
        var many = Assess(overdue: [Task("a", 1), Task("bb", 4)]).Reasons.Single();

        one.Text.Should().Be("1 overdue task (oldest 1 working day late)");
        many.Text.Should().Be("2 overdue tasks (oldest 4 working days late)");
    }

    [Fact]
    public void Several_late_tasks_are_critical_but_only_those_past_the_grace_count_toward_it()
    {
        Assess(overdue: [Task("a", 3), Task("bb", 3)]).Health.Should().Be("AtRisk");
        Assess(overdue: [Task("a", 3), Task("bb", 3), Task("ccc", 3)]).Health.Should().Be("Critical");
        // Three tasks, but two are still inside the grace.
        Assess(overdue: [Task("a", 3), Task("bb", 1), Task("ccc", 2)]).Health.Should().Be("AtRisk");
    }

    [Theory]
    [InlineData(4, "AtRisk")]
    [InlineData(5, "Critical")]
    [InlineData(9, "Critical")]
    public void A_blocker_becomes_critical_at_the_configured_working_days(int daysBlocked, string expected)
    {
        Assess(blocked: [Task("Stuck", daysBlocked)]).Health.Should().Be(expected);
    }

    [Fact]
    public void The_thresholds_are_configurable()
    {
        _thresholds.OverdueCriticalBusinessDays = 2;
        Assess(overdue: [Task("Late", 2)]).Health.Should().Be("Critical");

        _thresholds.OverdueCriticalBusinessDays = 5;
        _thresholds.CriticalOverdueTasks = 1;
        Assess(overdue: [Task("Late", 3)]).Health.Should().Be("Critical");
        Assess(overdue: [Task("Late", 2)]).Health.Should().Be("AtRisk"); // inside the grace

        _thresholds.OverdueGraceBusinessDays = 0;
        Assess(overdue: [Task("Late", 1)]).Health.Should().Be("Critical");

        _thresholds.BlockerCriticalBusinessDays = 2;
        Assess(blocked: [Task("Stuck", 3)]).Health.Should().Be("Critical");
    }

    [Fact]
    public void A_long_blocker_and_a_recent_one_are_reported_separately()
    {
        var result = Assess(blocked: [Task("Old", 8), Task("New", 1)]);

        result.Reasons.Should().Contain(r => r.Kind == "blocked_long" && r.Count == 1 && r.Text.Contains("oldest 8"));
        result.Reasons.Should().Contain(r => r.Kind == "blocked" && r.Count == 1);
    }

    [Fact]
    public void Reasons_name_the_worst_tasks_first()
    {
        var result = Assess(overdue: [Task("a", 1), Task("bb", 9), Task("ccc", 4), Task("dddd", 6), Task("eeeee", 2)]);

        var reason = result.Reasons.Single();
        reason.Count.Should().Be(5, "the count covers every task");
        // Below the cap: every task comes through as an example, worst first — nothing is hidden
        // behind a "not shown" count the UI has no way to reveal.
        reason.Examples.Select(e => e.Days).Should().Equal(9, 6, 4, 2, 1);
    }

    [Fact]
    public void A_project_with_more_than_the_cap_still_reports_the_true_count_and_the_worst_examples()
    {
        var tasks = Enumerable.Range(1, 55).Select(i => Task($"task-{i}", i)).ToArray();

        var result = Assess(overdue: tasks);

        var reason = result.Reasons.Single();
        reason.Count.Should().Be(55, "the count covers every task even past the example cap");
        reason.Examples.Should().HaveCount(50);
        reason.Examples.First().Days.Should().Be(55, "worst (highest days late) first");
        reason.Examples.Last().Days.Should().Be(6, "the 50 worst, not the first 50 encountered");
    }

    [Fact]
    public void Every_reason_says_what_to_do()
    {
        var result = Assess(
            overdue: [Task("Late", 3)], dueSoon: [Task("Soon", 1)], blocked: [Task("Stuck", 7), Task("Fresh", 1)]);

        result.Reasons.Select(r => r.Kind).Should().Equal("overdue", "blocked_long", "blocked", "due_soon");
        result.Reasons.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Action));
        result.Reasons.Single(r => r.Kind == "overdue").Action.Should().Be("Finish, re-date or reassign");
        result.Reasons.Single(r => r.Kind == "blocked_long").Action.Should().Be("Unblock or escalate");
    }

    [Fact]
    public void Next_steps_cover_every_reason_and_say_what_to_do()
    {
        var result = Assess(
            overdue: [Task("Late", 3)], dueSoon: [Task("Soon", 1), Task("Sooner", 2)], blocked: [Task("Stuck", 7)]);

        result.NextSteps.Should().HaveCount(3);
        result.NextSteps[0].Should().Contain("overdue").And.Contain("3 working days late");
        result.NextSteps.Should().Contain(s => s.Contains("Unblock") && s.Contains("5+ working days"));
        result.NextSteps.Should().Contain(s => s.Contains("2 tasks due within 3 days"));
    }

    [Fact]
    public void A_fresh_blocker_plus_something_due_soon_is_a_watch_item_not_critical()
    {
        Assess(blocked: [Task("Fresh", 1)], dueSoon: [Task("Soon", 2)]).Health.Should().Be("AtRisk");
    }
}

public class BusinessDaysBetweenTests
{
    [Fact]
    public void Counts_weekdays_after_the_start_up_to_and_including_the_end()
    {
        var monday = new DateOnly(2026, 9, 28);

        BusinessDays.Between(monday, monday).Should().Be(0);
        BusinessDays.Between(monday, monday.AddDays(1)).Should().Be(1);
        BusinessDays.Between(monday, monday.AddDays(4)).Should().Be(4);  // Tue..Fri
        BusinessDays.Between(monday, monday.AddDays(7)).Should().Be(5);  // weekend skipped
    }

    [Fact]
    public void Is_the_inverse_of_Add()
    {
        var start = new DateOnly(2026, 10, 2); // Friday
        foreach (var n in new[] { 1, 2, 5, 9 })
            BusinessDays.Between(start, BusinessDays.Add(start, n)).Should().Be(n);
    }

    [Fact]
    public void Is_zero_when_the_end_is_not_after_the_start()
    {
        BusinessDays.Between(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 1)).Should().Be(0);
    }
}
