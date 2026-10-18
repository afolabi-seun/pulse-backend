using Pulse.Application.Common.Interfaces;
using Pulse.Application.Estimation;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Estimation;

public class EstimateApprovalEscalationScannerTests
{
    // A Wednesday, mid-morning, inside the working day (08:00–18:00 UTC).
    private static readonly DateTime Submitted = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IEstimationRepository> _estimation = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly OverworkThresholds _thresholds = new();

    public EstimateApprovalEscalationScannerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _engineers.Setup(r => r.ListByRoleAsync(It.IsAny<string>(), default)).ReturnsAsync([]);
    }

    private EstimateApprovalEscalationScanner CreateScanner(DateTime now) => new(
        _estimation.Object, _tasks.Object, _engineers.Object, _teams.Object, _projects.Object,
        _settings.Object, _thresholds, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), new FixedClock(now));

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed record Scene(PulseTask Task, Engineer Assignee, Engineer? TeamLead, Engineer Head, Engineer Pmo, TaskEstimationSession Session);

    /// <summary>A task whose project is owned by a team (with a lead, unless <paramref name="withLead"/> is false) in the Engineering department,
    /// a head of that department, a PMO member, and an estimate submitted at <paramref name="submittedAt"/>.</summary>
    private Scene SeedPending(DateTime submittedAt, bool withLead = true, bool withHead = true)
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var teamLead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 20, 14);
        if (withLead) team.SetTeamLead(teamLead.Id);
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);
        var pmo = Engineer.Create("Pmo", "pmo@x.io", "hash", Roles.ProjectManager, 20, 14);

        var project = Project.Create("Proj", ownerTeamId: team.Id);
        var task = PulseTask.Create("Task", 0, project.Id, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assignee.Id, Guid.NewGuid());

        _projects.Setup(p => p.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(teamLead.Id, default)).ReturnsAsync(teamLead);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(withHead ? [assignee, teamLead, head] : [assignee, teamLead]);
        _engineers.Setup(r => r.ListByRoleAsync(Roles.ProjectManager, default)).ReturnsAsync([pmo]);

        var session = TaskEstimationSession.Create(task.Id);
        session.Reveal();
        session.SubmitForApproval(5, Guid.NewGuid(), submittedAt);
        _estimation.Setup(e => e.ListPendingApprovalsAsync(default)).ReturnsAsync([session]);

        return new Scene(task, assignee, withLead ? teamLead : null, head, pmo, session);
    }

    private void VerifyNotified(Engineer who, Times times) =>
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == who.Id && x.Kind == NotificationKind.EstimateApprovalEscalated), default), times);

    // ── Tier 2: the owning department's head, after a working hour ─────────────

    [Fact]
    public async Task Nothing_happens_before_the_team_lead_has_had_a_working_hour()
    {
        var scene = SeedPending(Submitted);

        await CreateScanner(Submitted.AddMinutes(30)).RunAsync();

        scene.Session.EscalatedToHead.Should().BeFalse();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task After_an_hour_the_department_head_is_added_and_told_once()
    {
        var scene = SeedPending(Submitted);

        await CreateScanner(Submitted.AddHours(1)).RunAsync();

        scene.Session.EscalatedToHead.Should().BeTrue();
        scene.Session.EscalatedToHeadAt.Should().Be(Submitted.AddHours(1));
        scene.Session.EscalatedToPmo.Should().BeFalse("the PMO comes a working hour after the head");
        VerifyNotified(scene.Head, Times.Once());
        _emailQueue.Verify(q => q.Enqueue(scene.Head.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task The_team_lead_who_already_knows_is_not_told_again()
    {
        var scene = SeedPending(Submitted);

        await CreateScanner(Submitted.AddHours(1)).RunAsync();

        VerifyNotified(scene.TeamLead!, Times.Never());
    }

    [Fact]
    public async Task The_clock_stops_overnight_and_over_the_weekend()
    {
        // 17:30 on a Friday: half an hour of the working day is left.
        var friday = new DateTime(2026, 10, 16, 17, 30, 0, DateTimeKind.Utc);
        var scene = SeedPending(friday);

        await CreateScanner(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc)).RunAsync(); // Saturday noon
        scene.Session.EscalatedToHead.Should().BeFalse("a Saturday is not working time");

        await CreateScanner(new DateTime(2026, 10, 19, 8, 15, 0, DateTimeKind.Utc)).RunAsync(); // Monday 08:15, 45 minutes worked
        scene.Session.EscalatedToHead.Should().BeFalse();

        await CreateScanner(new DateTime(2026, 10, 19, 8, 30, 0, DateTimeKind.Utc)).RunAsync(); // Monday 08:30, a full working hour
        scene.Session.EscalatedToHead.Should().BeTrue();
    }

    // ── Tier 3: the PMO, a working hour after the head ─────────────────────────

    [Fact]
    public async Task A_working_hour_after_the_head_was_added_the_pmo_is_added_and_told()
    {
        var scene = SeedPending(Submitted);
        await CreateScanner(Submitted.AddHours(1)).RunAsync(); // head added
        _notifications.Invocations.Clear();

        await CreateScanner(Submitted.AddHours(1).AddMinutes(30)).RunAsync();
        scene.Session.EscalatedToPmo.Should().BeFalse("only half an hour since the head was added");

        await CreateScanner(Submitted.AddHours(2)).RunAsync();

        scene.Session.EscalatedToPmo.Should().BeTrue();
        VerifyNotified(scene.Pmo, Times.Once());
        VerifyNotified(scene.Head, Times.Never());
        VerifyNotified(scene.TeamLead!, Times.Never());
    }

    [Fact]
    public async Task A_session_that_has_reached_the_pmo_is_left_alone()
    {
        var scene = SeedPending(Submitted);
        scene.Session.EscalateToHead(Submitted.AddHours(1));
        scene.Session.EscalateToPmo();
        // the repository only returns sessions that have not reached the PMO, so a finished one never arrives
        _estimation.Setup(e => e.ListPendingApprovalsAsync(default)).ReturnsAsync([]);

        await CreateScanner(Submitted.AddHours(9)).RunAsync();

        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    // ── Starting points other than the team lead ───────────────────────────────

    [Fact]
    public async Task With_no_team_lead_the_head_is_asked_from_the_start_and_the_pmo_a_working_hour_later()
    {
        var scene = SeedPending(Submitted, withLead: false);

        await CreateScanner(Submitted.AddMinutes(30)).RunAsync();
        scene.Session.EscalatedToPmo.Should().BeFalse();
        scene.Session.EscalatedToHead.Should().BeFalse("it already resolves to the head, so there is no team-lead tier to escalate past");

        await CreateScanner(Submitted.AddHours(1)).RunAsync();

        scene.Session.EscalatedToPmo.Should().BeTrue();
        VerifyNotified(scene.Pmo, Times.Once());
        VerifyNotified(scene.Head, Times.Never());
    }

    [Fact]
    public async Task With_neither_a_lead_nor_a_head_the_pmo_was_asked_from_the_start_so_there_is_nothing_to_escalate()
    {
        var scene = SeedPending(Submitted, withLead: false, withHead: false);

        await CreateScanner(Submitted.AddHours(5)).RunAsync();

        scene.Session.EscalatedToPmo.Should().BeFalse();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
    }

    // ── The owning department, not the assignee's ─────────────────────────────

    [Fact]
    public async Task The_head_told_is_the_one_of_the_department_that_owns_the_project_not_the_assignees()
    {
        var scene = SeedPending(Submitted);

        // The assignee works in Design, but the project belongs to Engineering's Platform team (seeded above).
        var designTeam = Team.Create("Design");
        designTeam.SetDepartment("Design");
        var designHead = Engineer.Create("Design head", "dh@x.io", "hash", Roles.HeadOfDesign, 20, 14);
        designHead.AssignToTeam(designTeam.Id);
        scene.Assignee.AssignToTeam(designTeam.Id);
        var platform = (await _teams.Object.ListAllAsync(default)).Single();
        _teams.Setup(r => r.GetByIdAsync(designTeam.Id, default)).ReturnsAsync(designTeam);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([platform, designTeam]);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([scene.Assignee, scene.TeamLead!, scene.Head, designHead]);

        await CreateScanner(Submitted.AddHours(1)).RunAsync();

        VerifyNotified(scene.Head, Times.Once());
        VerifyNotified(designHead, Times.Never());
    }
}
