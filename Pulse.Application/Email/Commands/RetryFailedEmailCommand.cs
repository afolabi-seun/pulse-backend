using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Email.Commands;

public record RetryFailedEmailCommand(Guid Id) : IRequest<ServiceResult<FailedEmailDto>>;

public class RetryFailedEmailHandler : IRequestHandler<RetryFailedEmailCommand, ServiceResult<FailedEmailDto>>
{
    private readonly IFailedEmailRepository _repo;
    private readonly IDirectEmailSender _email;

    public RetryFailedEmailHandler(IFailedEmailRepository repo, IDirectEmailSender email)
    {
        _repo  = repo;
        _email = email;
    }

    public async Task<ServiceResult<FailedEmailDto>> Handle(RetryFailedEmailCommand cmd, CancellationToken ct)
    {
        var record = await _repo.GetByIdAsync(cmd.Id, ct);
        if (record is null)
            return ServiceResult<FailedEmailDto>.Fail("NOT_FOUND", "Failed email record not found.");

        if (record.IsResolved)
            return ServiceResult<FailedEmailDto>.Fail("ALREADY_RESOLVED", "Email already resolved.");

        try
        {
            await _email.SendAsync(record.To, record.Subject, record.HtmlBody, ct);
            record.MarkResolved();
            await _repo.SaveChangesAsync(ct);
            return ServiceResult<FailedEmailDto>.Ok(FailedEmailDto.From(record));
        }
        catch (Exception ex)
        {
            record.RecordRetryFailure(ex.Message);
            await _repo.SaveChangesAsync(ct);
            return ServiceResult<FailedEmailDto>.Fail("RETRY_FAILED", "Retry failed — check logs. The attempt count has been updated.");
        }
    }
}
