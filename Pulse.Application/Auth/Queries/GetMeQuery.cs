using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Queries;

/// <summary>
/// Returns the caller's own current role, permissions, and resolved capabilities — the same
/// shape login/refresh return, without rotating tokens. Paired with the "role.changed" SignalR
/// event: an admin changing someone's role pushes that event to the affected user, whose client
/// re-fetches this endpoint to pick up the change without forcing a re-login. See
/// docs/rbac-consolidation.md (Phase 16f).
/// </summary>
public record GetMeQuery(Guid UserId) : IRequest<ServiceResult<AuthUserDto>>;

public class GetMeHandler : IRequestHandler<GetMeQuery, ServiceResult<AuthUserDto>>
{
    private readonly IEngineerRepository _engineers;

    public GetMeHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<AuthUserDto>> Handle(GetMeQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.UserId, ct);
        if (engineer is null)
            return ServiceResult<AuthUserDto>.Fail("NOT_FOUND", "User not found.");

        return ServiceResult<AuthUserDto>.Ok(new AuthUserDto(
            engineer.Id, engineer.Name, engineer.Email, engineer.Role,
            PermissionService.For(engineer.Role), CapabilityRegistry.ResolveFor(engineer.Role)));
    }
}
