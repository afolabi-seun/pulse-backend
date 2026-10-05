using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Users.Queries;

public record GetUserQuery(Guid UserId) : IRequest<ServiceResult<UserDto>>;

public class GetUserHandler : IRequestHandler<GetUserQuery, ServiceResult<UserDto>>
{
    private readonly IEngineerRepository _engineers;

    public GetUserHandler(IEngineerRepository engineers) => _engineers = engineers;

    public async Task<ServiceResult<UserDto>> Handle(GetUserQuery query, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(query.UserId, ct);
        return engineer is null
            ? ServiceResult<UserDto>.Fail("NOT_FOUND", $"User '{query.UserId}' not found.")
            : ServiceResult<UserDto>.Ok(UserDto.From(engineer));
    }
}
