using System.Text;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Notifications.Queries;

public record ListNotificationsQuery(
    Guid ActorId,
    bool UnreadOnly,
    int Limit,
    string? Cursor) : IRequest<ServiceResult<PagedResultDto<NotificationDto>>>;

public class ListNotificationsHandler : IRequestHandler<ListNotificationsQuery, ServiceResult<PagedResultDto<NotificationDto>>>
{
    private readonly INotificationRepository _notifications;

    public ListNotificationsHandler(INotificationRepository notifications) => _notifications = notifications;

    public async Task<ServiceResult<PagedResultDto<NotificationDto>>> Handle(ListNotificationsQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);
        var raw = await _notifications.ListForUserAsync(query.ActorId, query.UnreadOnly, limit + 1, query.Cursor, ct);

        var hasMore = raw.Count > limit;
        var page = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        string? nextCursor = null;
        if (hasMore)
        {
            var last = page[^1];
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.CreatedAt:O}|{last.Id}"));
        }

        return ServiceResult<PagedResultDto<NotificationDto>>.Ok(
            new PagedResultDto<NotificationDto>(page.Select(NotificationDto.From).ToList(), nextCursor, hasMore));
    }
}
