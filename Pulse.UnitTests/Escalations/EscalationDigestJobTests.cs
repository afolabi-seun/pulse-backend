using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations;
using Pulse.Domain.Engineers;
using Pulse.Domain.Escalations;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Teams;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;
using TaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Escalations;

public class EscalationDigestJobTests
{
    // Wednesday 14 Oct 2026, 09:00 UTC (the digest runs each weekday morning).
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IEscalationEventRepository> _events = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly List<EscalationEvent> _fired = [];
    private readonly List<PulseTask> _taskList = [];
    private readonly Dictionary<Guid, DateTime> _lastDigest = [];
    private readonly List<(string To, string Subject, string Body)> _sent = [];

    private readonly Engineer _head = Engineer.Create("Hari Head", "hari@test.io", "hash", Roles.HeadOfRnD, 20, 14);
    private readonly Engineer _pmo = Engineer.Create("Paula Pmo", "paula@test.io", "hash", Roles.HeadOfPmo, 20, 14);
    private readonly Engineer _pm = Engineer.Create("Priya PM", "priya@test.io", "hash", Roles.ProjectManager, 20, 14);
    private readonly Engineer _ada = Engineer.Create("Ada Assignee", "ada@test.io", "hash", Roles.Engineer, 20, 14);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    public EscalationDigestJobTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://cadence.test");
        _access.Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Engineer>());
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfRnD, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _head });
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfPmo, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _pmo });
        _engineers.Setup(e => e.ListByRoleAsync(Roles.ProjectManager, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _pm });
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _ada });

        _projects.Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid>());
        _projects.Setup(p => p.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [_projectId] = "Mobile App" });

        _notifications.Setup(n => n.GetLastCreatedAtByUserAsync(NotificationKind.EscalationDigest, It.IsAny<CancellationToken>())).ReturnsAsync(_lastDigest);
        _events.Setup(e => e.ListFiredSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime since, CancellationToken _) => _fired.Where(e => e.FiredAt > since).ToList());
        _tasks.Setup(t => t.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => _taskList.Where(t => ids.Contains(t.Id)).ToList());
        _emailQueue.Setup(q => q.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((to, subject, body) => _sent.Add((to, subject, body)));
    }

    private EscalationDigestJob Job() =>
        new(_events.Object, _tasks.Object, _projects.Object, _engineers.Object, _teams.Object, _access.Object, _notifications.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object, new FixedClock(Now));

    /// <summary>A live, assigned task with an escalation event of the given level fired <paramref name="firedHoursAgo"/> hours ago.</summary>
    private PulseTask Add(string title, DateOnly due, EscalationLevel level, double firedHoursAgo = 2, Guid? projectId = null)
    {
        var task = PulseTask.Create(title, 3, projectId ?? _projectId, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)));
        task.Assign(_ada.Id, Guid.NewGuid());
        task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria, task.Points, due, Guid.NewGuid());
        _taskList.Add(task);
        Fire(task, level, firedHoursAgo);
        return task;
    }

    private void Fire(PulseTask task, EscalationLevel level, double hoursAgo)
    {
        var ev = EscalationEvent.Record(task.Id, level);
        typeof(EscalationEvent).GetProperty("FiredAt")!.SetValue(ev, Now.AddHours(-hoursAgo));
        _fired.Add(ev);
    }

    private static void SetStatus(PulseTask task, TaskStatus status) => typeof(PulseTask).GetProperty("Status")!.SetValue(task, status);

    private IEnumerable<(string To, string Subject, string Body)> MailTo(Engineer e) => _sent.Where(m => m.To == e.Email);

    private static readonly DateOnly Tomorrow = new(2026, 10, 15);
    private static readonly DateOnly TwoDaysAgo = new(2026, 10, 12);

    [Fact]
    public async Task A_department_head_gets_the_3_day_and_overdue_tasks_but_not_a_1_day_one()
    {
        Add("Three-day task", new DateOnly(2026, 10, 17), EscalationLevel.TMinus3);
        Add("Overdue task", TwoDaysAgo, EscalationLevel.Overdue);
        Add("One-day task", Tomorrow, EscalationLevel.TMinus1);

        await Job().RunAsync();

        var mail = MailTo(_head).Should().ContainSingle().Subject;
        mail.Body.Should().Contain("Three-day task").And.Contain("Overdue task").And.NotContain("One-day task");
        mail.Subject.Should().Be("Due-date digest: 2 tasks need attention");
    }

    [Fact]
    public async Task The_PMO_gets_the_1_day_and_overdue_tasks_but_not_a_3_day_one()
    {
        Add("Three-day task", new DateOnly(2026, 10, 17), EscalationLevel.TMinus3);
        Add("Overdue task", TwoDaysAgo, EscalationLevel.Overdue);
        Add("One-day task", Tomorrow, EscalationLevel.TMinus1);

        await Job().RunAsync();

        foreach (var person in new[] { _pmo, _pm })
        {
            var mail = MailTo(person).Should().ContainSingle().Subject;
            mail.Body.Should().Contain("One-day task").And.Contain("Overdue task").And.NotContain("Three-day task");
        }
    }

    [Fact]
    public async Task Each_recipient_also_gets_one_in_app_alert_with_the_counts()
    {
        Add("Overdue task", TwoDaysAgo, EscalationLevel.Overdue);
        Add("One-day task", Tomorrow, EscalationLevel.TMinus1);

        await Job().RunAsync();

        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == _pmo.Id && x.Kind == NotificationKind.EscalationDigest && x.Payload!.Contains("\"count\":2") && x.Payload.Contains("\"overdue\":1")),
            It.IsAny<CancellationToken>()), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.Is<Notification>(x => x.UserId == _head.Id), It.IsAny<CancellationToken>()), Times.Once, "the overdue one");
    }

    [Fact]
    public async Task A_task_that_fired_twice_is_listed_once_in_the_state_it_is_in_now()
    {
        var task = Add("Moving task", TwoDaysAgo, EscalationLevel.TMinus3, firedHoursAgo: 60);
        Fire(task, EscalationLevel.TMinus1, 40);
        Fire(task, EscalationLevel.Overdue, 2);

        await Job().RunAsync();

        var body = MailTo(_pmo).Should().ContainSingle().Subject.Body;
        body.Split("Moving task").Length.Should().Be(2, "listed once");
        body.Should().Contain("overdue").And.Contain("2 days ago").And.NotContain("tomorrow");
    }

    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.InQa)]
    [InlineData(TaskStatus.Paused)]
    [InlineData(TaskStatus.Backlog)]
    public async Task A_task_that_is_no_longer_live_is_left_out(TaskStatus status)
    {
        var task = Add("Finished since", TwoDaysAgo, EscalationLevel.Overdue);
        SetStatus(task, status);

        await Job().RunAsync();

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Archived_tasks_QA_tasks_and_personal_to_dos_are_left_out()
    {
        var archived = Add("Archived", TwoDaysAgo, EscalationLevel.Overdue);
        archived.Archive(Guid.NewGuid(), "clean-up");
        var qa = Add("QA review", TwoDaysAgo, EscalationLevel.Overdue);
        typeof(PulseTask).GetProperty("ParentTaskId")!.SetValue(qa, Guid.NewGuid());
        var personalProject = Guid.NewGuid();
        Add("Private to-do", TwoDaysAgo, EscalationLevel.Overdue, projectId: personalProject);
        _projects.Setup(p => p.GetPersonalProjectIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { personalProject });

        await Job().RunAsync();

        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_projects_the_person_can_open_are_included()
    {
        Add("Visible", TwoDaysAgo, EscalationLevel.Overdue);
        var other = Guid.NewGuid();
        Add("Elsewhere", TwoDaysAgo, EscalationLevel.Overdue, projectId: other);
        _access.Setup(a => a.CanAccessProjectAsync(other, _head.Id, Roles.HeadOfRnD, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Job().RunAsync();

        var body = MailTo(_head).Should().ContainSingle().Subject.Body;
        body.Should().Contain("Visible").And.NotContain("Elsewhere");
        MailTo(_pmo).Single().Body.Should().Contain("Elsewhere", "the PMO is organisation-wide");
    }

    [Fact]
    public async Task It_covers_what_fired_since_the_persons_last_digest_and_not_what_was_already_sent()
    {
        Add("Already covered", TwoDaysAgo, EscalationLevel.Overdue, firedHoursAgo: 30);
        Add("New since", TwoDaysAgo, EscalationLevel.Overdue, firedHoursAgo: 2);
        _lastDigest[_head.Id] = Now.AddHours(-24);     // the head's last digest was yesterday morning; the PMO has never had one

        await Job().RunAsync();

        var head = MailTo(_head).Should().ContainSingle().Subject.Body;
        head.Should().Contain("New since").And.NotContain("Already covered");
        MailTo(_pmo).Single().Body.Should().Contain("Already covered").And.Contain("New since", "no earlier digest: the last 3 days");
    }

    [Fact]
    public async Task The_first_digest_goes_back_three_days_and_no_further()
    {
        Add("Four days old", TwoDaysAgo, EscalationLevel.Overdue, firedHoursAgo: 96);
        Add("Two days old", TwoDaysAgo, EscalationLevel.Overdue, firedHoursAgo: 48);

        await Job().RunAsync();

        var body = MailTo(_pmo).Should().ContainSingle().Subject.Body;
        body.Should().Contain("Two days old").And.NotContain("Four days old");
    }

    [Fact]
    public async Task Nothing_is_sent_when_nothing_is_relevant()
    {
        await Job().RunAsync();

        _sent.Should().BeEmpty();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_email_groups_by_project_names_the_assignee_and_how_far_off_each_date_is_and_escapes_titles()
    {
        Add("Fix <b>login</b> & logout", Tomorrow, EscalationLevel.TMinus1);

        await Job().RunAsync();

        var mail = MailTo(_pmo).Should().ContainSingle().Subject;
        mail.Subject.Should().Be("Due-date digest: 1 task needs attention", "singular wording");
        mail.Body.Should().Contain("Mobile App (1)").And.Contain("Ada Assignee").And.Contain("due <strong>tomorrow</strong>")
            .And.Contain("Fix &lt;b&gt;login&lt;/b&gt; &amp; logout").And.NotContain("<b>login</b>")
            .And.Contain("https://cadence.test/escalations");
    }

    // ── A head named on the owning team ───────────────────────────────────────

    [Fact]
    public async Task When_the_owning_team_names_a_head_only_that_head_hears_about_its_tasks()
    {
        var felix = Engineer.Create("Felix Onen", "felix@test.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfInfraDevOps, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { felix });
        var infra = Team.Create("Infra");
        infra.SetHead(felix.Id);
        var project = Project.Create("Infra work", ownerTeamId: infra.Id);
        _projects.Setup(p => p.GetByIdAsync(project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _teams.Setup(t => t.GetByIdAsync(infra.Id, It.IsAny<CancellationToken>())).ReturnsAsync(infra);
        Add("Infra task", TwoDaysAgo, EscalationLevel.Overdue, projectId: project.Id);

        await Job().RunAsync();

        MailTo(felix).Should().ContainSingle("Felix is the head the team names");
        MailTo(_head).Should().BeEmpty("Hari heads another department and only shares its label; he must not hear about this team's tasks");
        MailTo(_pmo).Should().ContainSingle("the PMO is unaffected");
    }

    [Fact]
    public async Task A_team_with_no_named_head_still_reaches_every_head_who_can_open_the_project()
    {
        var felix = Engineer.Create("Felix Onen", "felix@test.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(e => e.ListByRoleAsync(Roles.HeadOfInfraDevOps, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { felix });
        var team = Team.Create("Unassigned head");
        var project = Project.Create("Plain work", ownerTeamId: team.Id);
        _projects.Setup(p => p.GetByIdAsync(project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _teams.Setup(t => t.GetByIdAsync(team.Id, It.IsAny<CancellationToken>())).ReturnsAsync(team);
        Add("Plain task", TwoDaysAgo, EscalationLevel.Overdue, projectId: project.Id);

        await Job().RunAsync();

        MailTo(felix).Should().ContainSingle();
        MailTo(_head).Should().ContainSingle();
    }
}
