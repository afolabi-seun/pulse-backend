using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Users.Commands;

public record UpdateUserCommand(
    Guid UserId,
    string? Role,
    bool? IsActive,
    bool? UnlockAccount,
    Guid? TeamId,
    bool? IsQa,
    Discipline? Discipline,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<UserDto>>;

/// <summary>
/// Partial update of a user account: role change, activate/deactivate, or manual account unlock.
/// Each field is optional — only supplied fields are applied.
/// Audit entries are written for role changes and activation state changes.
/// </summary>
public class UpdateUserHandler : IRequestHandler<UpdateUserCommand, ServiceResult<UserDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IRealtimeNotifier _realtime;

    public UpdateUserHandler(IEngineerRepository engineers, ITeamRepository teams, IAuditLogRepository audit, IRealtimeNotifier realtime)
    {
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _realtime = realtime;
    }

    public async Task<ServiceResult<UserDto>> Handle(UpdateUserCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(cmd.UserId, ct);
        if (engineer is null)
            return ServiceResult<UserDto>.Fail("NOT_FOUND", $"User '{cmd.UserId}' not found.");

        // A QA engineer with no discipline is invisible to discipline-based QA routing (both the
        // auto-assign-on-send-to-QA match and the assignee picker's discipline filter) — fail
        // fast rather than silently produce a QA flag nobody's routing logic can ever act on.
        // Discipline can only ever be set here, never cleared, so the only way this combination
        // can arise is IsQa turning true while the engineer's existing discipline is still unset.
        var resultingIsQa = cmd.IsQa ?? engineer.IsQa;
        var resultingDiscipline = cmd.Discipline ?? engineer.Discipline;
        if (resultingIsQa && resultingDiscipline is null)
            return ServiceResult<UserDto>.Fail("BUSINESS_RULE_VIOLATION",
                "A discipline is required for a QA engineer — set one along with this change.");

        var roleChanged = cmd.Role is not null && cmd.Role != engineer.Role;

        try
        {
            if (roleChanged)
            {
                engineer.UpdateRole(cmd.Role!);
                await _audit.LogAsync("USER_ROLE_CHANGED", cmd.ActorId, cmd.IpAddress,
                    $"User {cmd.UserId} role → {cmd.Role}", ct);
            }

            if (cmd.IsActive.HasValue)
            {
                if (cmd.IsActive.Value && !engineer.IsActive)
                {
                    engineer.Reactivate();
                    await _audit.LogAsync("USER_REACTIVATED", cmd.ActorId, cmd.IpAddress,
                        $"User {cmd.UserId} reactivated", ct);
                }
                else if (!cmd.IsActive.Value && engineer.IsActive)
                {
                    engineer.Deactivate();
                    await _audit.LogAsync("USER_DEACTIVATED", cmd.ActorId, cmd.IpAddress,
                        $"User {cmd.UserId} deactivated", ct);
                }
            }

            if (cmd.UnlockAccount is true && engineer.IsLockedOut())
            {
                engineer.UnlockAccount();
                await _audit.LogAsync("USER_UNLOCKED", cmd.ActorId, cmd.IpAddress,
                    $"User {cmd.UserId} manually unlocked", ct);
            }

            if (cmd.TeamId is Guid teamId)
            {
                var team = await _teams.GetByIdAsync(teamId, ct);
                if (team is not null)
                {
                    engineer.SetTeam(team.Name);
                    engineer.AssignToTeam(team.Id);
                }
            }

            if (cmd.IsQa.HasValue)
                engineer.SetIsQa(cmd.IsQa.Value);

            if (cmd.Discipline.HasValue)
                engineer.SetDiscipline(cmd.Discipline.Value);
        }
        catch (DomainException ex)
        {
            return ServiceResult<UserDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _engineers.SaveChangesAsync(ct);

        // Fire after the save commits — an already-connected client re-fetches GET /auth/me on
        // this event, so it should only fire once the new role is actually persisted.
        if (roleChanged)
            await _realtime.SendRoleChangedAsync(cmd.UserId, ct);

        return ServiceResult<UserDto>.Ok(UserDto.From(engineer));
    }
}
