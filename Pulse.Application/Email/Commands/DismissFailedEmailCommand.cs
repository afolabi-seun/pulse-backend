using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Email.Commands;

public record DismissFailedEmailCommand(Guid Id) : IRequest<ServiceResult<FailedEmailDto>>;

public class DismissFailedEmailHandler : IRequestHandler<DismissFailedEmailCommand, ServiceResult<FailedEmailDto>>
{
    private readonly IFailedEmailRepository _repo;

    public DismissFailedEmailHandler(IFailedEmailRepository repo) => _repo = repo;

    public async Task<ServiceResult<FailedEmailDto>> Handle(DismissFailedEmailCommand cmd, CancellationToken ct)
    {
        var record = await _repo.GetByIdAsync(cmd.Id, ct);
        if (record is null)
            return ServiceResult<FailedEmailDto>.Fail("NOT_FOUND", "Failed email record not found.");

        if (!record.IsResolved)
        {
            record.MarkResolved();
            await _repo.SaveChangesAsync(ct);
        }

        return ServiceResult<FailedEmailDto>.Ok(FailedEmailDto.From(record));
    }
}
