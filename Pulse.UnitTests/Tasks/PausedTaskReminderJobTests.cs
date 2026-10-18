using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class PausedTaskReminderJobTests
{
    // Wednesday 14 Oct 2026, 09:00 UTC. Paused on Wednesday 7 Oct is exactly five working days earlier.
    private static readonly DateTime Now = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FiveWorkingDaysAgo = new(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly OverworkThresholds _thresholds = new();
    private readonly List<(string To, string Subject, string Body)> _sent = [];
    private readonly List<Notification> _created = [];

    private readonly Team _team = Team.Create("Platform");
    private readonly Engineer _lead = Engineer.Create("Lena Lead", "lena@test.io", "hash", Roles.TeamLead, 20, 14);
    private readonly Engineer _head = Engineer.Create("Hari Head", "hari@test.io", "hash", Roles.HeadOfRnD, 20, 14);
    private readonly Engineer _pm = Engineer.Create("Priya PM", "priya@test.io", "hash", Roles.ProjectManager, 20, 14);
    private readonly Engineer _ada = Engineer.Create("Ada Assignee", "ada@test.io", "hash", Roles.Engineer, 20, 14);
    private readonly Project _project;

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    public PausedTaskReminderJobTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _team.SetDepartment("Engineering");
        _team.SetTeamLead(_lead.Id);
        _head.AssignToTeam(_team.Id);
        _ada.AssignToTeam(_team.Id);
        _project = Project.Create("Mobile App", ownerTeamId: _team.Id);

        _projects.Setup(p => p.GetByIdAsync(_project.Id, default)).ReturnsAsync(_project);
        _projects.Setup(p => p.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [_project.Id] = "Mobile App" });
        _teams.Setup(t => t.GetByIdAsync(_team.Id, default)).ReturnsAsync(_team);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([_team]);
        _engineers.Setup(e => e.GetByIdAsync(_lead.Id, default)).ReturnsAsync(_lead);
        _engineers.Setup(e => e.GetByIdAsync(_ada.Id, default)).ReturnsAsync(_ada);
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync([_ada]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([_lead, _head, _pm, _ada]);
        _engineers.Setup(e => e.ListByRoleAsync(It.IsAny<string>(), default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListByRoleAsync(Roles.ProjectManager, default)).ReturnsAsync([_pm]);

        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), default)).Callback<Notification, CancellationToken>((n, _) => _created.Add(n)).Returns(Task.CompletedTask);
        _emailQueue.Setup(q => q.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((to, subject, body) => _sent.Add((to, subject, body)));
    }

    private PausedTaskReminderJob Job() => new(_tasks.Object, _projects.Object, _engineers.Object, _teams.Object,
        _settings.Object, _thresholds, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), new FixedClock(Now));

    private PulseTask PausedTask(string title, DateTime pausedAt, string? note = null, Guid? assignee = null)
    {
        var task = PulseTask.Create(title, 3, _project.Id, dueDate: DateOnly.FromDateTime(Now.AddDays(10)));
        task.Assign(assignee ?? _ada.Id, Guid.NewGuid());
        task.Pause(note, Guid.NewGuid(), at: pausedAt);
        return task;
    }

    private void Candidates(params PulseTask[] tasks) =>
        _tasks.Setup(t => t.GetPausedTasksAsync(default)).ReturnsAsync(tasks);

    [Fact]
    public async Task A_task_paused_five_working_days_is_raised_with_the_team_lead_department_head_and_pmo()
    {
        Candidates(PausedTask("Fix login", FiveWorkingDaysAgo, "Waiting on the API team"));

        await Job().RunAsync();

        _created.Select(n => n.UserId).Should().BeEquivalentTo([_lead.Id, _head.Id, _pm.Id]);
        _created.Should().OnlyContain(n => n.Kind == NotificationKind.PausedTaskReminder);
        _sent.Select(s => s.To).Should().BeEquivalentTo([_lead.Email, _head.Email, _pm.Email]);
        _sent[0].Body.Should().Contain("Fix login").And.Contain("Waiting on the API team").And.Contain("Ada Assignee").And.Contain("5 working days");
    }

    [Fact]
    public async Task The_person_who_paused_it_is_not_told_to_deal_with_their_own_pause()
    {
        Candidates(PausedTask("Fix login", FiveWorkingDaysAgo));

        await Job().RunAsync();

        _created.Should().NotContain(n => n.UserId == _ada.Id);
    }

    [Fact]
    public async Task A_lead_whose_own_task_is_paused_is_not_told_about_it_but_the_others_are()
    {
        Candidates(PausedTask("Lead's task", FiveWorkingDaysAgo, assignee: _lead.Id));

        await Job().RunAsync();

        _created.Select(n => n.UserId).Should().BeEquivalentTo([_head.Id, _pm.Id]);
    }

    [Fact]
    public async Task A_task_paused_less_than_five_working_days_is_left_alone()
    {
        Candidates(PausedTask("Fix login", new DateTime(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc))); // four working days

        await Job().RunAsync();

        _created.Should().BeEmpty();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Weekends_do_not_count_towards_the_five_days()
    {
        // Paused Friday 9 Oct: Mon 12, Tue 13 and Wed 14 are only three working days later, however many calendar days have passed.
        Candidates(PausedTask("Fix login", new DateTime(2026, 10, 9, 15, 0, 0, DateTimeKind.Utc)));

        await Job().RunAsync();

        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_already_reminded_this_week_is_not_reminded_again()
    {
        var task = PausedTask("Fix login", FiveWorkingDaysAgo);
        task.MarkPauseReminded(new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc)); // two working days ago
        Candidates(task);

        await Job().RunAsync();

        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_task_still_paused_a_further_five_working_days_after_the_last_reminder_is_raised_again()
    {
        var task = PausedTask("Fix login", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc));
        task.MarkPauseReminded(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc)); // five working days ago
        Candidates(task);

        await Job().RunAsync();

        _created.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_run_marks_the_tasks_as_reminded_and_saves_so_the_next_run_stays_quiet()
    {
        var task = PausedTask("Fix login", FiveWorkingDaysAgo);
        Candidates(task);

        await Job().RunAsync();

        task.PauseRemindedAt.Should().Be(Now);
        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Once);

        _created.Clear();
        await Job().RunAsync();
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task Several_paused_tasks_give_each_person_one_alert_and_one_email_listing_them_all()
    {
        Candidates(PausedTask("Fix login", FiveWorkingDaysAgo), PausedTask("Fix signup", FiveWorkingDaysAgo.AddDays(-1)));

        await Job().RunAsync();

        _created.Count(n => n.UserId == _lead.Id).Should().Be(1);
        var leadMail = _sent.Single(s => s.To == _lead.Email);
        leadMail.Subject.Should().Be("Paused tasks need a decision: 2 tasks");
        leadMail.Body.Should().Contain("Fix login").And.Contain("Fix signup");
    }

    [Fact]
    public async Task A_reminder_for_the_task_changes_nothing_but_the_reminder_stamp()
    {
        var task = PausedTask("Fix login", FiveWorkingDaysAgo);
        Candidates(task);

        await Job().RunAsync();

        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Paused, "this only reminds, it never reassigns or resumes");
        task.AssigneeId.Should().Be(_ada.Id);
    }

    [Fact]
    public async Task Resuming_and_pausing_again_starts_a_fresh_reminder_cycle()
    {
        var task = PausedTask("Fix login", FiveWorkingDaysAgo);
        task.MarkPauseReminded(Now);

        task.Resume(Guid.NewGuid());
        task.PauseRemindedAt.Should().BeNull();

        task.Pause("again", Guid.NewGuid());
        task.PauseRemindedAt.Should().BeNull();
    }

    [Fact]
    public async Task Nothing_to_do_when_no_task_is_paused()
    {
        Candidates();

        await Job().RunAsync();

        _created.Should().BeEmpty();
        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task When_the_owning_team_names_a_head_other_heads_who_share_its_department_label_are_not_told()
    {
        // _head (R&D) sits in the same department as the team, but the team names Felix as its head.
        var felix = Engineer.Create("Felix Onen", "felix@test.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(felix.Id, default)).ReturnsAsync(felix);
        _team.SetHead(felix.Id);
        Candidates(PausedTask("Fix login", FiveWorkingDaysAgo));

        await Job().RunAsync();

        _created.Select(n => n.UserId).Should().BeEquivalentTo([_lead.Id, felix.Id, _pm.Id]);
        _created.Should().NotContain(n => n.UserId == _head.Id);
    }
}
