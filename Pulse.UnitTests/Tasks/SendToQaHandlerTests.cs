using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Projects;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class SendToQaHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly OverworkThresholds _thresholds = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();

    public SendToQaHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _projects
            .Setup(p => p.ListMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProjectMemberDto>());
        // Default: nobody active org-wide. FindQaEngineerAsync's final tier always calls this,
        // even when discipline is null — tests that care about it override with their own setup.
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Engineer>());
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _tasks.Setup(t => t.GetNextTaskNumberAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private SendToQaHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _projects.Object, _teams.Object, _audit.Object, _access.Object, _thresholds,
            _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object, _checkIns.Object);

    private static PulseTask ReadyForQaTask(Discipline? discipline = null)
    {
        var task = PulseTask.Create("Ship the thing", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)));
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        if (discipline.HasValue) task.SetDiscipline(discipline.Value);
        return task;
    }

    [Fact]
    public async Task SendToQa_records_a_check_in_entry_for_the_engineer_who_built_it()
    {
        var task = ReadyForQaTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        CheckIn? added = null;
        _checkIns.Setup(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()))
            .Callback<CheckIn, CancellationToken>((ci, _) => added = ci).Returns(Task.CompletedTask);

        // Sent by someone else (PMO) — the entry still belongs to the task's assignee.
        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        added.Should().NotBeNull();
        added!.EngineerId.Should().Be(task.AssigneeId!.Value);
        added.ProjectId.Should().Be(task.ProjectId);
        added.Completed.Should().Be("Sent to QA: Ship the thing");
    }

    [Fact]
    public async Task SendToQa_computes_QaTask_dueDate_from_configured_lead_time_not_the_original_due_date()
    {
        _thresholds.QaLeadTimeDays = 2;
        var task = ReadyForQaTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.DueDate.Should().NotBe(task.DueDate,
            "the QA task's due date is independent of the original task's due date");
        captured.DueDate.Should().Be(BusinessDays.Add(DateOnly.FromDateTime(DateTime.UtcNow), 2));
    }

    [Fact]
    public async Task SendToQa_prefers_a_QA_engineer_whose_discipline_matches()
    {
        var task = ReadyForQaTask(Discipline.Backend);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var qaFrontend = Engineer.Create("QA Frontend", "qafe@test.io", "hash", Roles.Engineer, 20, 14);
        qaFrontend.SetIsQa(true);
        qaFrontend.SetDiscipline(Discipline.Frontend);
        var qaBackend = Engineer.Create("QA Backend", "qabe@test.io", "hash", Roles.Engineer, 20, 14);
        qaBackend.SetIsQa(true);
        qaBackend.SetDiscipline(Discipline.Backend);

        _projects.Setup(p => p.ListMembersAsync(task.ProjectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(qaFrontend.Id, qaFrontend.Name, qaFrontend.Email, Roles.Engineer, DateTime.UtcNow),
            new ProjectMemberDto(qaBackend.Id, qaBackend.Name, qaBackend.Email, Roles.Engineer, DateTime.UtcNow),
        });
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new[] { qaFrontend, qaBackend });
        _engineers.Setup(e => e.GetByIdAsync(qaBackend.Id, default)).ReturnsAsync(qaBackend);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(qaBackend.Id);

        // The assigned reviewer is notified in-app (same TaskAssigned kind any other
        // assignment uses) and by email — not the "nobody's reviewing this" QaUnassigned kind.
        _notifications.Verify(n => n.AddAsync(
            It.Is<Pulse.Domain.Notifications.Notification>(x =>
                x.UserId == qaBackend.Id && x.Kind == Pulse.Domain.Notifications.NotificationKind.TaskAssigned),
            It.IsAny<CancellationToken>()), Times.Once);
        _realtime.Verify(r => r.SendNotificationAsync(qaBackend.Id, It.IsAny<Pulse.Application.Notifications.NotificationDto>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _emailQueue.Verify(e => e.Enqueue(qaBackend.Email, It.Is<string>(s => s.Contains("QA review assigned")), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task SendToQa_widens_org_wide_when_no_discipline_match_is_on_the_project_and_grants_access()
    {
        var task = ReadyForQaTask(Discipline.Backend);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        // On the project, but wrong discipline — should be skipped in favor of the org-wide match.
        var qaFrontendOnProject = Engineer.Create("QA Frontend OnProject", "qafeop@test.io", "hash", Roles.Engineer, 20, 14);
        qaFrontendOnProject.SetIsQa(true);
        qaFrontendOnProject.SetDiscipline(Discipline.Frontend);
        _projects.Setup(p => p.ListMembersAsync(task.ProjectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(qaFrontendOnProject.Id, qaFrontendOnProject.Name, qaFrontendOnProject.Email, Roles.Engineer, DateTime.UtcNow),
        });
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new[] { qaFrontendOnProject });

        // Not on the project, but the right discipline — should be found and granted access.
        var qaBackendElsewhere = Engineer.Create("QA Backend Elsewhere", "qabee@test.io", "hash", Roles.Engineer, 20, 14);
        qaBackendElsewhere.SetIsQa(true);
        qaBackendElsewhere.SetDiscipline(Discipline.Backend);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { qaFrontendOnProject, qaBackendElsewhere });
        _engineers.Setup(e => e.GetByIdAsync(qaBackendElsewhere.Id, default)).ReturnsAsync(qaBackendElsewhere);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(qaBackendElsewhere.Id);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, qaBackendElsewhere.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToQa_falls_back_to_a_discipline_mismatched_QA_engineer_on_the_project_when_nobody_matches_anywhere()
    {
        var task = ReadyForQaTask(Discipline.Backend);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var qaFrontendOnProject = Engineer.Create("QA Frontend Only", "qafo@test.io", "hash", Roles.Engineer, 20, 14);
        qaFrontendOnProject.SetIsQa(true);
        qaFrontendOnProject.SetDiscipline(Discipline.Frontend);
        _projects.Setup(p => p.ListMembersAsync(task.ProjectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(qaFrontendOnProject.Id, qaFrontendOnProject.Name, qaFrontendOnProject.Email, Roles.Engineer, DateTime.UtcNow),
        });
        _engineers.Setup(e => e.GetByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new[] { qaFrontendOnProject });
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { qaFrontendOnProject });
        _engineers.Setup(e => e.GetByIdAsync(qaFrontendOnProject.Id, default)).ReturnsAsync(qaFrontendOnProject);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(qaFrontendOnProject.Id);
        _projects.Verify(p => p.AddMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "already a project member — no membership grant needed");
    }

    [Fact]
    public async Task SendToQa_leaves_QaTask_unassigned_when_no_QA_engineer_is_on_the_project()
    {
        // ListMembersAsync default setup (fixture ctor) returns an empty roster.
        var task = ReadyForQaTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().BeNull();
        captured.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog,
            "an unassigned QA task should sit in Backlog, not silently escalate as Active");

        // Nobody was auto-assigned to review it — the original submitter (who is allowed to
        // accept their own unassigned QA task) should be told that option exists.
        _notifications.Verify(n => n.AddAsync(
            It.Is<Pulse.Domain.Notifications.Notification>(x =>
                x.UserId == task.AssigneeId && x.Kind == Pulse.Domain.Notifications.NotificationKind.QaUnassigned),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToQa_finds_a_QA_engineer_org_wide_even_when_the_task_has_no_discipline()
    {
        // Reported bug: a properly configured QA engineer (IsQa + discipline both set) never got
        // auto-assigned, because the original task itself had no discipline set — a common,
        // expected case (see QaAssignmentPolicy) — which used to skip the org-wide widening
        // entirely and fall back to a project-members-only search, same as before that fix.
        var task = ReadyForQaTask(); // no discipline
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var qaElsewhere = Engineer.Create("QA Elsewhere", "qa_elsewhere@test.io", "hash", Roles.Engineer, 20, 14);
        qaElsewhere.SetIsQa(true);
        qaElsewhere.SetDiscipline(Discipline.Backend);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { qaElsewhere });
        _engineers.Setup(e => e.GetByIdAsync(qaElsewhere.Id, default)).ReturnsAsync(qaElsewhere);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(qaElsewhere.Id);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, qaElsewhere.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToQa_assigns_the_explicitly_chosen_QA_engineer_and_grants_project_access_when_not_already_a_member()
    {
        // Discipline set, so this only exercises the plain IsQa + explicit-pick path — the
        // no-discipline role/department restriction has its own tests below.
        var task = ReadyForQaTask(Discipline.Backend);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        // Would have been the auto-pick's choice too, were one not explicitly provided — proves
        // the explicit pick isn't just coincidentally matching the auto algorithm.
        var autoPick = Engineer.Create("QA Auto", "qaauto@test.io", "hash", Roles.Engineer, 20, 14);
        autoPick.SetIsQa(true);
        autoPick.SetDiscipline(Discipline.Backend);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { autoPick });

        var chosen = Engineer.Create("QA Chosen", "qachosen@test.io", "hash", Roles.Engineer, 20, 14);
        chosen.SetIsQa(true);
        chosen.SetDiscipline(Discipline.Frontend);
        _engineers.Setup(e => e.GetByIdAsync(chosen.Id, default)).ReturnsAsync(chosen);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null, QaEngineerId: chosen.Id), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(chosen.Id, "the explicit pick overrides the auto-assign recommendation");
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, chosen.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToQa_rejects_an_explicit_pick_that_is_not_a_QA_engineer()
    {
        var task = ReadyForQaTask(Discipline.Backend);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var nonQaEngineer = Engineer.Create("Not QA", "notqa@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(nonQaEngineer.Id, default)).ReturnsAsync(nonQaEngineer);

        var result = await CreateHandler().Handle(new SendToQaCommand(task.Id, Guid.NewGuid(), null, QaEngineerId: nonQaEngineer.Id), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()), Times.Never,
            "the rejected pick must not create the QA task at all");
    }

    [Fact]
    public async Task SendToQa_rejects_an_explicit_pick_for_a_no_discipline_task_when_actor_is_not_an_allowed_head()
    {
        var task = ReadyForQaTask(); // no discipline
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var qaEngineer = Engineer.Create("QA Eng", "qaeng@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);

        var result = await CreateHandler().Handle(
            new SendToQaCommand(task.Id, Guid.NewGuid(), null, Roles.ProjectManager, QaEngineerId: qaEngineer.Id), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task SendToQa_allows_an_allowed_head_to_explicitly_pick_a_Functional_department_engineer_for_a_no_discipline_task()
    {
        var task = ReadyForQaTask(); // no discipline
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var team = Pulse.Domain.Teams.Team.Create("Ops", department: "Functional");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var qaEngineer = Engineer.Create("QA Eng", "qaeng2@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        qaEngineer.AssignToTeam(team.Id);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);

        PulseTask? captured = null;
        _tasks.Setup(r => r.AddAsync(It.IsAny<PulseTask>(), It.IsAny<CancellationToken>()))
            .Callback<PulseTask, CancellationToken>((t, _) => captured = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(
            new SendToQaCommand(task.Id, Guid.NewGuid(), null, Roles.HeadOfFunctional, QaEngineerId: qaEngineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        captured!.AssigneeId.Should().Be(qaEngineer.Id);
    }
}
