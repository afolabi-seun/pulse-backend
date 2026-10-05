using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Feedback.Queries;

public record GetFeedbackPatternsQuery(Guid ActorId, string ActorRole, string? IpAddress)
    : IRequest<ServiceResult<FeedbackPatternsDto>>;

public class GetFeedbackPatternsHandler : IRequestHandler<GetFeedbackPatternsQuery, ServiceResult<FeedbackPatternsDto>>
{
    private const int MinDistinctSources = 3;

    private readonly IFeedbackRepository _feedback;
    private readonly IAuditLogRepository _auditLog;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetFeedbackPatternsHandler(IFeedbackRepository feedback, IAuditLogRepository auditLog,
        IEngineerRepository engineers, ITeamRepository teams)
    {
        _feedback = feedback;
        _auditLog = auditLog;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<FeedbackPatternsDto>> Handle(GetFeedbackPatternsQuery query, CancellationToken ct)
    {
        // Same people as the entries tab: a department head sees their own department, PMO/HR everyone.
        var dept = await FeedbackScope.ResolveDepartmentAsync(query.ActorRole, query.ActorId, _engineers, _teams, ct);

        var summaries = dept is not null
            ? await _feedback.GetWeekSummariesByDepartmentAsync(dept, ct)
            : await _feedback.GetWeekSummariesAsync(ct);

        var weeks = summaries
            .Where(s => s.DistinctSources >= MinDistinctSources)
            .Select(s => new FeedbackPatternDto(s.WeekOf, s.TotalCount, s.DistinctSources))
            .ToList();
        var hidden = summaries.Count - weeks.Count;

        var eligible = await CountEligiblePeopleAsync(dept, ct);

        await _auditLog.LogAsync("FEEDBACK_PATTERNS_READ", query.ActorId, query.IpAddress,
            $"Read {(dept is null ? "org-wide" : $"department ({dept})")} patterns summary ({weeks.Count} qualifying weeks)", ct);

        return ServiceResult<FeedbackPatternsDto>.Ok(new FeedbackPatternsDto(weeks, hidden, eligible, dept ?? "Organisation"));
    }

    /// <summary>Active people on a team who can give feedback — not department heads or the org-wide read-only
    /// roles, who have no one to give it to — within the scope.</summary>
    private async Task<int> CountEligiblePeopleAsync(string? dept, CancellationToken ct)
    {
        var departmentByTeam = (await _teams.ListAllAsync(ct)).ToDictionary(t => t.Id, t => t.Department);
        return (await _engineers.ListActiveAsync(ct)).Count(e =>
            e.TeamId is Guid teamId
            && !Roles.HeadRoles.Contains(e.Role)
            && !Roles.IsOrgReadOnlyViewer(e.Role)
            && (dept is null || (departmentByTeam.TryGetValue(teamId, out var d) && d == dept)));
    }
}
