using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Email.Queries;

public record ListFailedEmailsQuery(bool IncludeResolved, int Limit, Guid? Cursor)
    : IRequest<ServiceResult<FailedEmailPageDto>>;

public class ListFailedEmailsHandler : IRequestHandler<ListFailedEmailsQuery, ServiceResult<FailedEmailPageDto>>
{
    private readonly IFailedEmailRepository _repo;

    public ListFailedEmailsHandler(IFailedEmailRepository repo) => _repo = repo;

    public async Task<ServiceResult<FailedEmailPageDto>> Handle(ListFailedEmailsQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);
        var rows = await _repo.ListAsync(query.IncludeResolved, limit + 1, query.Cursor, ct);

        var hasMore = rows.Count > limit;
        var page = (hasMore ? rows.Take(limit) : rows).Select(FailedEmailDto.From).ToList();
        var nextCursor = hasMore ? page.Last().Id : (Guid?)null;

        return ServiceResult<FailedEmailPageDto>.Ok(new FailedEmailPageDto(page, nextCursor, hasMore));
    }
}
