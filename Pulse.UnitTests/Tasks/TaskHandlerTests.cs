using Pulse.Application.Comments.Commands;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Application.Tasks;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using Pulse.Domain.Escalations;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class TaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEpicRepository> _epics = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<ITaskDependencyRepository> _dependencies = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ISprintRepository> _sprints = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<ITeamRepository> _teams = new();

    public TaskHandlerTests()
    {
        _dependencies
            .Setup(r => r.GetDependentsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Pulse.Domain.Tasks.TaskDependency>());
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        // Default: caller is authorized. Access-control behaviour is covered by ProjectAccessPolicyTests
        // and the dedicated forbidden-path test below.
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _checkIns
            .Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CheckIn>());
        _projects
            .Setup(p => p.ListMembersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Pulse.Application.Projects.ProjectMemberDto>());
        // Default: no accessible engineers — mention-parsing (FlagBlockerHandler) has nothing to scan.
        _access
            .Setup(a => a.GetAccessibleEngineerIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());
    }

    private static PulseTask ActiveTask(Guid? assigneeId = null)
    {
        var task = PulseTask.Create("Test task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        if (assigneeId.HasValue)
            task.Assign(assigneeId.Value, Guid.NewGuid());
        return task;
    }

    private static Engineer ActiveEngineer() =>
        Engineer.Create("Dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);

    // ── UpdateTaskHandler ─────────────────────────────────────────────────────

    private UpdateTaskHandler CreateUpdateHandler() =>
        new(_tasks.Object, _engineers.Object, _audit.Object, _escalationEvents.Object, _epics.Object,
            _dependencies.Object, _settings.Object, _access.Object, _projects.Object, _sprints.Object,
            _checkIns.Object, _teams.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    [Fact]
    public async Task UpdateTask_returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(Guid.NewGuid(), "New title", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task UpdateTask_returns_FORBIDDEN_when_actor_cannot_access_the_project()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _access
            .Setup(a => a.CanAccessTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, "New title", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task UpdateTask_allows_assignee_even_without_project_access()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        // Mirrors production: CanAccessTaskAsync returns true for the assignee even without
        // separate project access — this handler-level test only needs the boundary to hold,
        // not to re-derive that logic (covered directly by ProjectAccessPolicyTests).
        _access
            .Setup(a => a.CanAccessTaskAsync(task.Id, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, null, null, true, null, null, null, null, null, null, null, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task UpdateTask_changes_title_and_logs_audit()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, "Updated title", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        task.Title.Should().Be("Updated title");
        _audit.Verify(a => a.LogAsync("TASK_UPDATED", It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), default), Times.Once);
    }

    [Fact]
    public async Task UpdateTask_marks_done_and_transitions_status()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, null, null, true, null, null, null, null, null, null, null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public async Task UpdateTask_sets_priority_when_supplied()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null, Priority: 5), default);

        result.IsSuccess.Should().BeTrue();
        task.Priority.Should().Be(5);
    }

    [Fact]
    public async Task UpdateTask_leaves_priority_untouched_when_not_supplied()
    {
        var task = ActiveTask();
        task.SetPriority(2);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: "Renamed", Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        task.Priority.Should().Be(2, "an unrelated edit must not clear an existing priority");
    }

    [Fact]
    public async Task QaAccepted_notifies_the_original_assignee_in_app_and_by_email()
    {
        var assignee = Engineer.Create("Grace Liu", "grace@test.io", "hash", Roles.Engineer, 20, 14);
        var parent = PulseTask.Create("Original task", 3, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        parent.SetRequiresQa(true);
        parent.Assign(assignee.Id, Guid.NewGuid());
        parent.SendToQa(Guid.NewGuid());
        var qaTask = PulseTask.Create("[QA] Original task", 3, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));
        qaTask.SetParentTaskId(parent.Id);

        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: qaTask.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: true, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        parent.Status.Should().Be(DomainTaskStatus.Done);
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == assignee.Id && x.Kind == NotificationKind.QaAccepted),
            default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(assignee.Email, It.Is<string>(s => s.Contains(parent.Title)), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task MarkDone_creates_a_checkin_for_the_tasks_project_when_actor_has_none_there_today()
    {
        // Default fixture setup already has GetByEngineerAndDateAsync return null (unconfigured).
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var actorId = Guid.NewGuid();

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: true, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: actorId, IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        _checkIns.Verify(c => c.AddAsync(
            It.Is<CheckIn>(ci => ci.EngineerId == actorId && ci.ProjectId == task.ProjectId && ci.Completed.Contains(task.Title)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkDone_appends_to_todays_checkin_for_that_project_instead_of_creating_a_second_one()
    {
        // A second task finishing in a project that already has today's check-in extends it
        // (so nothing from either completion is lost) rather than creating a duplicate entry or
        // silently doing nothing.
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var actorId = Guid.NewGuid();
        var existingCheckIn = CheckIn.Submit(actorId, DateOnly.FromDateTime(DateTime.UtcNow), "Completed: Earlier task", "", null, task.ProjectId);
        _checkIns
            .Setup(c => c.GetByEngineerAndDateAsync(actorId, DateOnly.FromDateTime(DateTime.UtcNow), task.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingCheckIn);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: true, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: actorId, IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        _checkIns.Verify(c => c.AddAsync(It.IsAny<CheckIn>(), It.IsAny<CancellationToken>()), Times.Never);
        existingCheckIn.Completed.Should().Contain("Earlier task").And.Contain(task.Title);
    }

    [Fact]
    public async Task UpdateTask_rejects_assigning_a_QA_task_to_a_non_QA_engineer()
    {
        // Parent has a discipline set, so this only exercises the plain IsQa requirement —
        // the no-discipline role/department restriction has its own tests below.
        var parent = ActiveTask();
        parent.SetDiscipline(Discipline.Backend);
        var qaTask = ActiveTask();
        qaTask.SetParentTaskId(parent.Id);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var nonQaEngineer = ActiveEngineer();
        _engineers.Setup(e => e.GetByIdAsync(nonQaEngineer.Id, default)).ReturnsAsync(nonQaEngineer);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: qaTask.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: nonQaEngineer.Id, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        qaTask.AssigneeId.Should().BeNull("the rejected assignment must not have applied");
    }

    [Fact]
    public async Task UpdateTask_allows_assigning_a_QA_task_to_a_QA_engineer()
    {
        var parent = ActiveTask();
        parent.SetDiscipline(Discipline.Backend);
        var qaTask = ActiveTask();
        qaTask.SetParentTaskId(parent.Id);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var qaEngineer = ActiveEngineer();
        qaEngineer.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: qaTask.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: qaEngineer.Id, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null, ActorRole: Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        qaTask.AssigneeId.Should().Be(qaEngineer.Id);
    }

    [Fact]
    public async Task UpdateTask_rejects_assigning_a_no_discipline_QA_task_when_actor_is_not_an_allowed_head()
    {
        var parent = ActiveTask(); // no discipline set
        var qaTask = ActiveTask();
        qaTask.SetParentTaskId(parent.Id);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var qaEngineer = ActiveEngineer();
        qaEngineer.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: qaTask.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: qaEngineer.Id, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null, ActorRole: Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task UpdateTask_allows_HeadOfFunctional_to_assign_a_no_discipline_QA_task_to_a_Functional_department_engineer()
    {
        var parent = ActiveTask(); // no discipline set
        var qaTask = ActiveTask();
        qaTask.SetParentTaskId(parent.Id);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);

        var team = Pulse.Domain.Teams.Team.Create("Ops", department: "Functional");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var qaEngineer = ActiveEngineer();
        qaEngineer.SetIsQa(true);
        qaEngineer.AssignToTeam(team.Id);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: qaTask.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: qaEngineer.Id, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null, ActorRole: Roles.HeadOfFunctional), default);

        result.IsSuccess.Should().BeTrue();
        qaTask.AssigneeId.Should().Be(qaEngineer.Id);
    }

    [Fact]
    public async Task UpdateTask_ignores_title_change_but_succeeds_when_task_is_done()
    {
        var task = ActiveTask();
        task.MarkDone(Guid.NewGuid());
        var originalTitle = task.Title;
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: "New title", Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Title.Should().Be(originalTitle);
    }

    [Fact]
    public async Task UpdateTask_allows_toggling_requiresQa_on_a_done_task()
    {
        var task = ActiveTask();
        task.MarkDone(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(
                TaskId: task.Id, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
                EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
                AssigneeId: null, Type: null, MarkDone: null, SprintId: null, RemoveFromSprint: null,
                Status: null, BlockerReason: null, PauseNote: null, RequiresQa: true, Discipline: null,
                ActorId: Guid.NewGuid(), IpAddress: null), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.RequiresQa.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateTask_clears_escalation_events_on_reassignment()
    {
        var newAssignee = ActiveEngineer();
        var task = ActiveTask(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(newAssignee.Id, default)).ReturnsAsync(newAssignee);

        await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, newAssignee.Id, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null, Roles.TeamLead), default);

        _escalationEvents.Verify(e => e.ClearForTaskAsync(task.Id, default), Times.Once);
    }

    [Fact]
    public async Task UpdateTask_clears_escalation_events_on_due_date_change()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var newDue = task.DueDate!.Value.AddDays(7);

        await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, newDue, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null, DueDateChangeReason: "Scope grew"), default);

        _escalationEvents.Verify(e => e.ClearForTaskAsync(task.Id, default), Times.Once);
    }

    [Fact]
    public async Task UpdateTask_rejects_a_due_date_change_without_a_reason()
    {
        var task = ActiveTask();
        var original = task.DueDate;
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, original!.Value.AddDays(7), null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null,
                DueDateChangeReason: "  "), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        task.DueDate.Should().Be(original);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateTask_records_the_trimmed_reason_on_the_due_date_history_entry()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, task.DueDate!.Value.AddDays(7), null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null,
                DueDateChangeReason: "  Client asked for more time  "), default);

        task.History.Single(h => h.Field == "due_date").Reason.Should().Be("Client asked for more time");
    }

    // ── Changing the points on a started task needs a reason ────────────────────

    private static UpdateTaskCommand PointsEdit(Guid taskId, int points, string? reason = null) =>
        new(taskId, null, null, null, null, null, null, points, null, null, null, null, null, null, null, null, null, null, null, null,
            Guid.NewGuid(), null, PointsChangeReason: reason);

    [Fact]
    public async Task UpdateTask_rejects_a_points_change_on_a_started_task_without_a_reason()
    {
        var task = ActiveTask(Guid.NewGuid());   // assigned and pointed, so Active
        task.Status.Should().Be(Domain.Tasks.TaskStatus.Active);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 8, "  "), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        task.Points.Should().Be(3);
        _tasks.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task UpdateTask_records_the_trimmed_reason_on_the_points_history_entry()
    {
        var task = ActiveTask(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 8, "  Spike showed the work is bigger  "), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(8);
        var entry = task.History.Single(h => h.Field == "points");
        entry.OldValue.Should().Be("3");
        entry.NewValue.Should().Be("8");
        entry.Reason.Should().Be("Spike showed the work is bigger");
    }

    [Fact]
    public async Task UpdateTask_rejects_a_points_reason_over_500_characters()
    {
        var task = ActiveTask(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 8, new string('x', 501)), default);

        result.ErrorCode.Should().Be("VALIDATION_ERROR");
        task.Points.Should().Be(3);
    }

    [Fact]
    public async Task UpdateTask_does_not_ask_for_a_reason_while_the_task_is_still_in_backlog()
    {
        var task = ActiveTask();                 // no assignee, so still Backlog
        task.Status.Should().Be(Domain.Tasks.TaskStatus.Backlog);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 5), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(5);
    }

    [Fact]
    public async Task UpdateTask_does_not_ask_for_a_reason_for_the_first_estimate_on_a_started_task()
    {
        var task = ActiveTask(Guid.NewGuid());
        task.SetPoints(0, Guid.NewGuid());       // a started task that has no estimate yet
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 5), default);

        result.IsSuccess.Should().BeTrue();
        task.Points.Should().Be(5);
    }

    [Fact]
    public async Task UpdateTask_does_not_ask_for_a_reason_when_the_points_are_unchanged()
    {
        var task = ActiveTask(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateUpdateHandler().Handle(PointsEdit(task.Id, 3), default);

        result.IsSuccess.Should().BeTrue();
        task.History.Should().NotContain(h => h.Field == "points");
    }

    [Fact]
    public async Task UpdateTask_does_not_clear_events_when_due_date_unchanged()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        // Pass the same due date — no change
        await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, "New title", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null), default);

        _escalationEvents.Verify(e => e.ClearForTaskAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task UpdateTask_logs_TASK_REASSIGNED_when_assignee_changes()
    {
        var newAssignee = ActiveEngineer();
        var task = ActiveTask(Guid.NewGuid()); // task has a different current assignee
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(newAssignee.Id, default)).ReturnsAsync(newAssignee);
        var actorId = Guid.NewGuid();

        await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, newAssignee.Id, null, null, null, null, null, null, null, null, null, actorId, null, Roles.TeamLead), default);

        _audit.Verify(a => a.LogAsync("TASK_REASSIGNED", actorId, It.IsAny<string?>(), It.IsAny<string?>(), default), Times.Once);
    }

    [Fact]
    public async Task UpdateTask_below_team_lead_reassigning_to_someone_else_is_silently_corrected_to_self()
    {
        // Defense-in-depth: PATCH /tasks/{id} is gated to TeamLeadOrAbove at the controller today,
        // so a below-Team-Lead ActorRole can't actually reach this handler through the real API —
        // but the handler must not trust that pairing blindly, so this exercises the guard directly.
        var actorId = Guid.NewGuid();
        var someoneElse = ActiveEngineer();
        var task = ActiveTask(actorId); // actor is already the task's assignee, so access passes
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(someoneElse.Id, default)).ReturnsAsync(someoneElse);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(ActiveEngineer());
        _access.Setup(a => a.CanAccessTaskAsync(task.Id, actorId, Roles.Engineer, default)).ReturnsAsync(true);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, someoneElse.Id, null, null, null, null, null, null, null, null, null, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(actorId);
    }

    [Fact]
    public async Task UpdateTask_returns_BUSINESS_RULE_VIOLATION_when_new_assignee_inactive()
    {
        var task = ActiveTask();
        var inactive = ActiveEngineer();
        inactive.Deactivate();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(inactive.Id, default)).ReturnsAsync(inactive);

        var result = await CreateUpdateHandler().Handle(
            new UpdateTaskCommand(task.Id, null, null, null, null, null, null, null, null, null, inactive.Id, null, null, null, null, null, null, null, null, null, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    // ── UpdateTaskHandler: sprint due-date suggestion ────────────────────────

    private static Pulse.Domain.Sprints.Sprint NewSprint(DateOnly? endDate = null) =>
        Pulse.Domain.Sprints.Sprint.Create(Guid.NewGuid(), null, "Sprint 1",
            DateOnly.FromDateTime(DateTime.UtcNow), endDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)));

    private static UpdateTaskCommand SprintAssignCommand(Guid taskId, Guid sprintId, Guid actorId, Guid? assigneeId = null, string actorRole = Roles.TeamLead) =>
        new(TaskId: taskId, Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
            EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
            AssigneeId: assigneeId, Type: null, MarkDone: null, SprintId: sprintId, RemoveFromSprint: null,
            Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
            ActorId: actorId, IpAddress: null, ActorRole: actorRole);

    [Fact]
    public async Task UpdateTask_returns_NOT_FOUND_when_sprint_missing()
    {
        var task = PulseTask.Create("No due date", 3, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Pulse.Domain.Sprints.Sprint?)null);

        var result = await CreateUpdateHandler().Handle(
            SprintAssignCommand(task.Id, Guid.NewGuid(), Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task UpdateTask_defaults_due_date_to_sprint_end_date_when_task_has_none()
    {
        var task = PulseTask.Create("No due date", 3, Guid.NewGuid());
        var sprintEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21));
        var sprint = NewSprint(sprintEnd);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(sprint.Id, default)).ReturnsAsync(sprint);

        var result = await CreateUpdateHandler().Handle(
            SprintAssignCommand(task.Id, sprint.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(sprintEnd);
        task.SprintId.Should().Be(sprint.Id);
    }

    [Fact]
    public async Task UpdateTask_does_not_override_an_existing_due_date_when_adding_to_sprint()
    {
        var existingDueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var task = PulseTask.Create("Has due date", 3, Guid.NewGuid(), dueDate: existingDueDate);
        var sprint = NewSprint(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21)));
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(sprint.Id, default)).ReturnsAsync(sprint);

        var result = await CreateUpdateHandler().Handle(
            SprintAssignCommand(task.Id, sprint.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        task.DueDate.Should().Be(existingDueDate);
    }

    [Fact]
    public async Task UpdateTask_can_assign_engineer_and_sprint_together_when_task_has_no_due_date()
    {
        var task = PulseTask.Create("No due date", 3, Guid.NewGuid());
        var engineer = ActiveEngineer();
        var sprint = NewSprint();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _sprints.Setup(r => r.GetByIdAsync(sprint.Id, default)).ReturnsAsync(sprint);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);

        var result = await CreateUpdateHandler().Handle(
            SprintAssignCommand(task.Id, sprint.Id, Guid.NewGuid(), assigneeId: engineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(engineer.Id);
        task.DueDate.Should().Be(sprint.EndDate);
    }

    // ── FlagBlockerHandler ────────────────────────────────────────────────────

    private FlagBlockerHandler CreateFlagBlockerHandler() =>
        new(_tasks.Object, _audit.Object, _realtime.Object, _epics.Object, _access.Object,
            _engineers.Object, _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    [Fact]
    public async Task FlagBlocker_returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(Guid.NewGuid(), "reason", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task FlagBlocker_returns_FORBIDDEN_when_caller_is_not_assignee_and_has_no_project_access()
    {
        var assigneeId = Guid.NewGuid();
        var differentActor = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, differentActor, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "reason", differentActor, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task FlagBlocker_allows_a_non_assignee_with_project_access_eg_PMO_or_a_department_head()
    {
        var assigneeId = Guid.NewGuid();
        var pmoActorId = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, pmoActorId, Roles.HeadOfPmo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Waiting on legal sign-off", pmoActorId, null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Blocked);
    }

    [Fact]
    public async Task FlagBlocker_sets_task_to_Blocked_and_pushes_realtime()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Waiting on API", actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Blocked);
        task.BlockerReason.Should().Be("Waiting on API");
        _realtime.Verify(r => r.SendTaskUpdatedAsync(actorId, It.IsAny<TaskDto>(), default), Times.Once);
    }

    [Fact]
    public async Task FlagBlocker_notifies_the_assignee_in_app_and_by_email_when_someone_else_flags_it()
    {
        var assignee = Engineer.Create("Grace Liu", "grace@test.io", "hash", Roles.Engineer, 20, 14);
        var flagger = Engineer.Create("Nina Torres", "nina@test.io", "hash", Roles.HeadOfPmo, 20, 14);
        var task = ActiveTask(assignee.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, flagger.Id, Roles.HeadOfPmo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _engineers.Setup(e => e.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(e => e.GetByIdAsync(flagger.Id, default)).ReturnsAsync(flagger);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Waiting on legal sign-off", flagger.Id, null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == assignee.Id && x.Kind == NotificationKind.BlockerFlagged),
            default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(assignee.Email, It.Is<string>(s => s.Contains(task.Title)), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FlagBlocker_does_not_notify_when_the_assignee_flags_their_own_blocker()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Waiting on API", actorId, null), default);

        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);
        _emailQueue.Verify(q => q.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FlagBlocker_notifies_a_project_member_mentioned_by_name_in_the_reason()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId); // self-flagged — no assignee notification, only the mention
        var mentioned = Engineer.Create("James Murphy", "james@test.io", "hash", Roles.Engineer, 20, 14);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(mentioned.Id, default)).ReturnsAsync(mentioned);
        _access
            .Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default))
            .ReturnsAsync(new[] { mentioned.Id });
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(mentioned.Id)), default))
            .ReturnsAsync(new[] { mentioned });

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Blocked — @James Murphy can you help?", actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == mentioned.Id && x.Kind == NotificationKind.Mentioned),
            default), Times.Once);
        _emailQueue.Verify(q => q.Enqueue(mentioned.Email, It.Is<string>(s => s.Contains(task.Title)), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task FlagBlocker_notifies_the_tasks_creator_even_with_no_other_project_access()
    {
        var actorId = Guid.NewGuid();
        var creator = Engineer.Create("Priya Filer", "priya.f@test.io", "hash", Roles.ProductManager, 20, 14);
        var task = PulseTask.Create("Test task", 3, Guid.NewGuid(), createdById: creator.Id);
        task.Assign(actorId, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(creator.Id, default)).ReturnsAsync(creator);
        // Access-set mock deliberately returns nothing — the creator must still be notified.
        _access.Setup(a => a.GetAccessibleEngineerIdsAsync(task.ProjectId, default)).ReturnsAsync(Array.Empty<Guid>());
        _engineers
            .Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(creator.Id)), default))
            .ReturnsAsync(new[] { creator });

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "Blocked — @Priya Filer can you unblock this?", actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        _notifications.Verify(n => n.AddAsync(
            It.Is<Notification>(x => x.UserId == creator.Id && x.Kind == NotificationKind.Mentioned),
            default), Times.Once);
    }

    [Fact]
    public async Task FlagBlocker_returns_BUSINESS_RULE_VIOLATION_when_task_is_done()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.MarkDone(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "reason", actorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task FlagBlocker_logs_audit_entry()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateFlagBlockerHandler().Handle(
            new FlagBlockerCommand(task.Id, "reason", actorId, "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("BLOCKER_FLAGGED", actorId, "1.2.3.4", It.IsAny<string?>(), default), Times.Once);
    }

    // ── ClearBlockerHandler ───────────────────────────────────────────────────

    private ClearBlockerHandler CreateClearBlockerHandler() =>
        new(_tasks.Object, _audit.Object, _epics.Object, _access.Object);

    [Fact]
    public async Task ClearBlocker_returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateClearBlockerHandler().Handle(
            new ClearBlockerCommand(Guid.NewGuid(), Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ClearBlocker_returns_FORBIDDEN_when_actor_is_not_assignee_and_cannot_access_project()
    {
        var assignee = Guid.NewGuid();
        var task = ActiveTask(assignee);
        task.FlagBlocker("reason", assignee);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateClearBlockerHandler().Handle(
            new ClearBlockerCommand(task.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.Status.Should().Be(DomainTaskStatus.Blocked); // unchanged
    }

    [Fact]
    public async Task ClearBlocker_sets_task_back_to_Active()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.FlagBlocker("reason", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateClearBlockerHandler().Handle(
            new ClearBlockerCommand(task.Id, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Active);
        task.BlockerReason.Should().BeNull();
    }

    [Fact]
    public async Task ClearBlocker_returns_BUSINESS_RULE_VIOLATION_when_task_not_blocked()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateClearBlockerHandler().Handle(
            new ClearBlockerCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task ClearBlocker_logs_audit_entry()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.FlagBlocker("reason", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateClearBlockerHandler().Handle(
            new ClearBlockerCommand(task.Id, actorId, "5.6.7.8"), default);

        _audit.Verify(a => a.LogAsync("BLOCKER_CLEARED", actorId, "5.6.7.8", It.IsAny<string?>(), default), Times.Once);
    }

    // ── PauseTaskHandler ──────────────────────────────────────────────────────

    private PauseTaskHandler CreatePauseTaskHandler() =>
        new(_tasks.Object, _audit.Object, _realtime.Object, _epics.Object, _access.Object);

    [Fact]
    public async Task PauseTask_returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(Guid.NewGuid(), "note", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task PauseTask_returns_FORBIDDEN_when_caller_is_not_assignee_and_cannot_access_project()
    {
        var assigneeId = Guid.NewGuid();
        var differentActor = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(task.Id, "note", differentActor, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task PauseTask_allows_a_non_assignee_head_with_project_access()
    {
        var assigneeId = Guid.NewGuid();
        var headId = Guid.NewGuid();
        var task = ActiveTask(assigneeId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, headId, Roles.HeadOfPmo, default))
            .ReturnsAsync(true);

        var result = await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(task.Id, "note", headId, null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Paused);
    }

    [Fact]
    public async Task PauseTask_sets_task_to_Paused_and_pushes_realtime()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(task.Id, "Reprioritized", actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Paused);
        task.PauseNote.Should().Be("Reprioritized");
        _realtime.Verify(r => r.SendTaskUpdatedAsync(actorId, It.IsAny<TaskDto>(), default), Times.Once);
    }

    [Fact]
    public async Task PauseTask_allows_pausing_a_blocked_task()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.FlagBlocker("reason", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(task.Id, null, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Paused);
    }

    [Fact]
    public async Task PauseTask_logs_audit_entry()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreatePauseTaskHandler().Handle(
            new PauseTaskCommand(task.Id, "note", actorId, "1.2.3.4"), default);

        _audit.Verify(a => a.LogAsync("TASK_PAUSED", actorId, "1.2.3.4", It.IsAny<string?>(), default), Times.Once);
    }

    // ── ResumeTaskHandler ─────────────────────────────────────────────────────

    private ResumeTaskHandler CreateResumeTaskHandler() =>
        new(_tasks.Object, _audit.Object, _realtime.Object, _epics.Object, _access.Object);

    [Fact]
    public async Task ResumeTask_returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateResumeTaskHandler().Handle(
            new ResumeTaskCommand(Guid.NewGuid(), Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task ResumeTask_returns_FORBIDDEN_when_actor_is_not_assignee_and_cannot_access_project()
    {
        var assignee = Guid.NewGuid();
        var task = ActiveTask(assignee);
        task.Pause("note", assignee);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateResumeTaskHandler().Handle(
            new ResumeTaskCommand(task.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.Status.Should().Be(DomainTaskStatus.Paused); // unchanged
    }

    [Fact]
    public async Task ResumeTask_sets_task_back_to_Active()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.Pause("note", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateResumeTaskHandler().Handle(
            new ResumeTaskCommand(task.Id, actorId, null), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(DomainTaskStatus.Active);
        task.PauseNote.Should().BeNull();
    }

    [Fact]
    public async Task ResumeTask_returns_BUSINESS_RULE_VIOLATION_when_task_not_paused()
    {
        var task = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateResumeTaskHandler().Handle(
            new ResumeTaskCommand(task.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task ResumeTask_logs_audit_entry()
    {
        var actorId = Guid.NewGuid();
        var task = ActiveTask(actorId);
        task.Pause("note", actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        await CreateResumeTaskHandler().Handle(
            new ResumeTaskCommand(task.Id, actorId, "5.6.7.8"), default);

        _audit.Verify(a => a.LogAsync("TASK_RESUMED", actorId, "5.6.7.8", It.IsAny<string?>(), default), Times.Once);
    }

    // ── AddTaskDependencyHandler ──────────────────────────────────────────────

    private AddTaskDependencyHandler CreateAddDependencyHandler() =>
        new(_tasks.Object, _dependencies.Object, _audit.Object, _access.Object);

    [Fact]
    public async Task AddDependency_allows_the_dependent_tasks_assignee_without_project_access()
    {
        var actorId = Guid.NewGuid();
        var dependent = ActiveTask(actorId);
        var blocking = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(blocking.Id, default)).ReturnsAsync(blocking);
        _tasks.Setup(r => r.GetByIdAsync(dependent.Id, default)).ReturnsAsync(dependent);
        _access
            .Setup(a => a.CanAccessProjectAsync(dependent.ProjectId, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _access
            .Setup(a => a.CanAccessProjectAsync(blocking.ProjectId, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateAddDependencyHandler().Handle(
            new AddTaskDependencyCommand(blocking.Id, dependent.Id, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AddDependency_returns_FORBIDDEN_when_actor_is_not_an_assignee_and_cannot_access_either_project()
    {
        var actorId = Guid.NewGuid();
        var dependent = ActiveTask();
        var blocking = ActiveTask();
        _tasks.Setup(r => r.GetByIdAsync(blocking.Id, default)).ReturnsAsync(blocking);
        _tasks.Setup(r => r.GetByIdAsync(dependent.Id, default)).ReturnsAsync(dependent);
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateAddDependencyHandler().Handle(
            new AddTaskDependencyCommand(blocking.Id, dependent.Id, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    // ── RemoveTaskDependencyHandler ───────────────────────────────────────────

    private RemoveTaskDependencyHandler CreateRemoveDependencyHandler() =>
        new(_dependencies.Object, _audit.Object, _tasks.Object, _access.Object);

    [Fact]
    public async Task RemoveDependency_allows_the_dependent_tasks_assignee_without_project_access()
    {
        var actorId = Guid.NewGuid();
        var dependent = ActiveTask(actorId);
        var blockingTaskId = Guid.NewGuid();
        _dependencies.Setup(d => d.ExistsAsync(blockingTaskId, dependent.Id, default)).ReturnsAsync(true);
        _tasks.Setup(r => r.GetByIdAsync(dependent.Id, default)).ReturnsAsync(dependent);
        _access
            .Setup(a => a.CanAccessProjectAsync(dependent.ProjectId, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateRemoveDependencyHandler().Handle(
            new RemoveTaskDependencyCommand(blockingTaskId, dependent.Id, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveDependency_returns_FORBIDDEN_when_actor_is_not_assignee_and_cannot_access_project()
    {
        var actorId = Guid.NewGuid();
        var dependent = ActiveTask();
        var blockingTaskId = Guid.NewGuid();
        _dependencies.Setup(d => d.ExistsAsync(blockingTaskId, dependent.Id, default)).ReturnsAsync(true);
        _tasks.Setup(r => r.GetByIdAsync(dependent.Id, default)).ReturnsAsync(dependent);
        _access
            .Setup(a => a.CanAccessProjectAsync(dependent.ProjectId, actorId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateRemoveDependencyHandler().Handle(
            new RemoveTaskDependencyCommand(blockingTaskId, dependent.Id, actorId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    // ── DeleteCommentHandler ──────────────────────────────────────────────────

    private readonly Mock<ITaskCommentRepository> _comments = new();

    private DeleteCommentHandler CreateDeleteCommentHandler() =>
        new(_comments.Object);

    private static TaskComment SomeComment(Guid? authorId = null) =>
        TaskComment.Create(Guid.NewGuid(), authorId ?? Guid.NewGuid(), "body");

    [Fact]
    public async Task DeleteComment_owner_can_delete_own_comment()
    {
        var authorId = Guid.NewGuid();
        var comment = SomeComment(authorId);
        _comments.Setup(r => r.GetByIdAsync(comment.Id, default)).ReturnsAsync(comment);

        var result = await CreateDeleteCommentHandler().Handle(
            new DeleteCommentCommand(comment.Id, authorId, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        _comments.Verify(r => r.DeleteAsync(comment, default), Times.Once);
    }

    [Fact]
    public async Task DeleteComment_engineer_cannot_delete_others_comment()
    {
        var comment = SomeComment();
        _comments.Setup(r => r.GetByIdAsync(comment.Id, default)).ReturnsAsync(comment);

        var result = await CreateDeleteCommentHandler().Handle(
            new DeleteCommentCommand(comment.Id, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Theory]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfDesign)]
    [InlineData(Roles.HeadOfPmo)]
    public async Task DeleteComment_new_head_roles_can_delete_any_comment(string role)
    {
        var comment = SomeComment();
        _comments.Setup(r => r.GetByIdAsync(comment.Id, default)).ReturnsAsync(comment);

        var result = await CreateDeleteCommentHandler().Handle(
            new DeleteCommentCommand(comment.Id, Guid.NewGuid(), role), default);

        result.IsSuccess.Should().BeTrue(because: $"{role} is a department head and must be able to moderate comments");
        _comments.Verify(r => r.DeleteAsync(comment, default), Times.Once);
    }
}
