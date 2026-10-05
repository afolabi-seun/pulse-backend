using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Users;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Users.Queries;

public record ListUsersQuery(int Limit, string? Cursor, string CallerRole, Guid CallerId, string? Role = null, string? Team = null, bool? IsActive = null, bool? IsQa = null) : IRequest<ServiceResult<PagedResultDto<UserDto>>>;

/// <summary>Returns users scoped to the caller's department. Roles.UserManagementGlobalRoles
/// (Head of PMO, Project Manager, Head of Product) see all users, org-wide.</summary>
public class ListUsersHandler : IRequestHandler<ListUsersQuery, ServiceResult<PagedResultDto<UserDto>>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;

    public ListUsersHandler(IEngineerRepository engineers, ITeamRepository teams)
    {
        _engineers = engineers;
        _teams     = teams;
    }

    public async Task<ServiceResult<PagedResultDto<UserDto>>> Handle(ListUsersQuery query, CancellationToken ct)
    {
        var limit  = Math.Clamp(query.Limit, 1, 100);
        var offset = query.Cursor is not null
            ? int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query.Cursor)))
            : 0;

        IReadOnlyList<Guid>? callerTeamIds = null;

        if (!Roles.UserManagementGlobalRoles.Contains(query.CallerRole))
        {
            var caller = await _engineers.GetByIdAsync(query.CallerId, ct);
            if (caller?.TeamId is Guid callerTeamId)
            {
                var callerTeam = await _teams.GetByIdAsync(callerTeamId, ct);
                if (callerTeam?.Department is string deptName)
                {
                    var allTeams = await _teams.ListAllAsync(ct);
                    callerTeamIds = allTeams
                        .Where(t => t.Department == deptName)
                        .Select(t => t.Id)
                        .ToList();
                }
                else
                {
                    callerTeamIds = [callerTeamId];
                }
            }
            // No team assigned → no scoping (callerTeamIds stays null, UI shows amber warning)
        }

        var raw     = await _engineers.ListAllPagedAsync(limit + 1, query.Cursor, query.Role, query.Team, query.IsActive, callerTeamIds, query.IsQa, ct);
        var hasMore = raw.Count > limit;
        var page    = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        var nextCursor = hasMore
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + limit).ToString()))
            : null;

        return ServiceResult<PagedResultDto<UserDto>>.Ok(
            new PagedResultDto<UserDto>(page.Select(UserDto.From).ToList(), nextCursor, hasMore));
    }
}
