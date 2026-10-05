using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class FailedEmailRepository : IFailedEmailRepository
{
    private readonly PulseDbContext _db;

    public FailedEmailRepository(PulseDbContext db) => _db = db;

    public async Task AddAsync(FailedEmail email, CancellationToken ct = default)
    {
        _db.FailedEmails.Add(email);
        await _db.SaveChangesAsync(ct);
    }

    public Task<FailedEmail?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.FailedEmails.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<FailedEmail>> ListAsync(
        bool includeResolved, int limit, Guid? afterId, CancellationToken ct = default)
    {
        var query = _db.FailedEmails.AsQueryable();

        if (!includeResolved) query = query.Where(e => !e.IsResolved);
        if (afterId.HasValue)
        {
            var pivot = await _db.FailedEmails
                .Where(e => e.Id == afterId.Value)
                .Select(e => e.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (pivot != default)
                query = query.Where(e => e.CreatedAt < pivot);
        }

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _db.SaveChangesAsync(ct);
}
