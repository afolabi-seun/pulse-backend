using Pulse.Domain.Email;

namespace Pulse.Application.Common.Interfaces;

public interface IFailedEmailRepository
{
    Task AddAsync(FailedEmail email, CancellationToken ct = default);
    Task<FailedEmail?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<FailedEmail>> ListAsync(bool includeResolved, int limit, Guid? afterId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
