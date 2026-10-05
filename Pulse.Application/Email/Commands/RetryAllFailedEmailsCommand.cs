using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Email.Commands;

public record RetryAllFailedEmailsCommand : IRequest<ServiceResult<RetryAllResult>>;

public record RetryAllResult(int Succeeded, int Failed);

public class RetryAllFailedEmailsHandler : IRequestHandler<RetryAllFailedEmailsCommand, ServiceResult<RetryAllResult>>
{
    private readonly IFailedEmailRepository _repo;
    private readonly IDirectEmailSender _email;

    public RetryAllFailedEmailsHandler(IFailedEmailRepository repo, IDirectEmailSender email)
    {
        _repo  = repo;
        _email = email;
    }

    public async Task<ServiceResult<RetryAllResult>> Handle(RetryAllFailedEmailsCommand _, CancellationToken ct)
    {
        var records = await _repo.ListAsync(includeResolved: false, limit: 500, afterId: null, ct);

        var succeeded = 0;
        var failed = 0;

        foreach (var record in records)
        {
            try
            {
                await _email.SendAsync(record.To, record.Subject, record.HtmlBody, ct);
                record.MarkResolved();
                succeeded++;
            }
            catch (Exception ex)
            {
                record.RecordRetryFailure(ex.Message);
                failed++;
            }
        }

        await _repo.SaveChangesAsync(ct);

        return ServiceResult<RetryAllResult>.Ok(new RetryAllResult(succeeded, failed));
    }
}
