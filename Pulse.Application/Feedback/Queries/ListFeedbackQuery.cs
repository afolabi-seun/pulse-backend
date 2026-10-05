using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Feedback.Queries;

public record ListFeedbackQuery(DateOnly? WeekOf, Guid ActorId, string ActorRole, string? IpAddress)
    : IRequest<ServiceResult<IReadOnlyList<FeedbackDto>>>;

public class ListFeedbackHandler : IRequestHandler<ListFeedbackQuery, ServiceResult<IReadOnlyList<FeedbackDto>>>
{
    private readonly IFeedbackRepository _feedback;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;
    private readonly IAuditLogRepository _auditLog;

    public ListFeedbackHandler(
        IFeedbackRepository feedback,
        IEngineerRepository engineers,
        ITeamRepository     teams,
        IAuditLogRepository auditLog)
    {
        _feedback  = feedback;
        _engineers = engineers;
        _teams     = teams;
        _auditLog  = auditLog;
    }

    public async Task<ServiceResult<IReadOnlyList<FeedbackDto>>> Handle(ListFeedbackQuery query, CancellationToken ct)
    {
        var dept = await FeedbackScope.ResolveDepartmentAsync(query.ActorRole, query.ActorId, _engineers, _teams, ct);

        var entries = dept is not null
            ? await _feedback.ListByDepartmentAsync(dept, query.WeekOf, ct)
            : await _feedback.ListAsync(query.WeekOf, ct);

        var replierIds = entries.Where(f => f.RepliedBy.HasValue).Select(f => f.RepliedBy!.Value).Distinct().ToList();
        var replierNames = replierIds.Count > 0
            ? (await _engineers.GetByIdsAsync(replierIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var dtos = entries
            .Select(f => FeedbackDto.From(f, f.RepliedBy.HasValue ? replierNames.GetValueOrDefault(f.RepliedBy.Value) : null))
            .ToList();

        await _auditLog.LogAsync("FEEDBACK_READ", query.ActorId, query.IpAddress,
            $"Read {entries.Count} feedback entries" +
            $"{(dept is not null ? $" (dept: {dept})" : string.Empty)}" +
            $"{(query.WeekOf.HasValue ? $" for week {query.WeekOf}" : string.Empty)}", ct);

        return ServiceResult<IReadOnlyList<FeedbackDto>>.Ok(dtos);
    }
}
