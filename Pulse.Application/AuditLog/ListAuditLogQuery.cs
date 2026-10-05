using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.AuditLog;

public record ListAuditLogQuery(
    long? Cursor,
    int Limit,
    Guid? ActorId,
    string? Action,
    DateTime? From,
    DateTime? To) : IRequest<ServiceResult<AuditLogPageDto>>;

/// <summary>
/// Returns a paginated, descending audit log slice. Cursor is the ID of the oldest entry on the previous page.
/// </summary>
public class ListAuditLogHandler : IRequestHandler<ListAuditLogQuery, ServiceResult<AuditLogPageDto>>
{
    private readonly IAuditLogRepository _repo;

    public ListAuditLogHandler(IAuditLogRepository repo) => _repo = repo;

    public async Task<ServiceResult<AuditLogPageDto>> Handle(ListAuditLogQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);

        // Fetch one extra to detect whether there is a next page
        var entries = await _repo.ListAsync(query.Cursor, limit + 1, query.ActorId, query.Action, query.From, query.To, ct);

        var hasMore = entries.Count > limit;
        var page = hasMore ? entries.Take(limit).ToList() : entries.ToList();
        var nextCursor = hasMore ? (long?)page.Last().Id : null;

        return ServiceResult<AuditLogPageDto>.Ok(new AuditLogPageDto(page, nextCursor, hasMore));
    }
}
