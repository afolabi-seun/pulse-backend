using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Escalations.Queries;

public record GetEscalationsQuery(Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<EscalationDto>>>;

public class GetEscalationsHandler : IRequestHandler<GetEscalationsQuery, ServiceResult<IReadOnlyList<EscalationDto>>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly OverworkThresholds _thresholds;
    private readonly IProjectRepository _projects;

    public GetEscalationsHandler(ITaskRepository tasks, IEngineerRepository engineers, ITeamRepository teams, OverworkThresholds thresholds, IProjectRepository projects)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _thresholds = thresholds;
        _projects = projects;
    }

    public async Task<ServiceResult<IReadOnlyList<EscalationDto>>> Handle(GetEscalationsQuery query, CancellationToken ct)
    {
        var lookahead = (int)Math.Ceiling(_thresholds.EscalationT3Days);
        var candidates = (await _tasks.GetEscalationCandidatesAsync(lookahead, ct)).AsEnumerable();

        // Ownership has moved to QA, the engineer has voluntarily paused the task, or there's no
        // assignee yet (Backlog) — matches the exclusion already applied to EscalationScanner.
        candidates = candidates.Where(t => t.Status is not (Domain.Tasks.TaskStatus.InQa or Domain.Tasks.TaskStatus.Paused or Domain.Tasks.TaskStatus.Backlog));

        // Non-PMO/PM heads see only escalations for their own department's engineers. Executive
        // has no department of their own, so — like PMO/PM/Product — always gets everything.
        var deptEngineerIds = query.ActorRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct or Roles.Executive or Roles.HR
            ? null
            : await DepartmentScope.EngineerIdsAsync(query.ActorId, _engineers, _teams, ct);
        if (deptEngineerIds is not null)
            candidates = candidates.Where(t => t.AssigneeId.HasValue && deptEngineerIds.Contains(t.AssigneeId.Value));

        var now = DateTime.UtcNow;
        candidates = candidates.ToList();
        var projectIds = candidates.Select(t => t.ProjectId).Distinct().ToList();
        var projectNameById = await _projects.GetNamesByIdsAsync(projectIds, ct);
        var projectCodeById = await _projects.GetCodesByIdsAsync(projectIds, ct);

        var escalations = candidates
            .Select(t =>
            {
                var level = EscalationLevelCalculator.Determine(t.DueDate!.Value, t.ActivatedAt, _thresholds, now);
                if (level is null) return null;

                var dueDateTime = t.DueDate.Value.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
                var daysUntilDue = (int)Math.Floor((dueDateTime - now).TotalDays);
                projectNameById.TryGetValue(t.ProjectId, out var projectName);
                var taskKey = projectCodeById.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null;
                return new EscalationDto(t.Id, t.Title, t.AssigneeId, t.DueDate, level.Value.ToString(), daysUntilDue, projectName, taskKey);
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.DaysUntilDue)
            .ToList();

        return ServiceResult<IReadOnlyList<EscalationDto>>.Ok(escalations);
    }
}
