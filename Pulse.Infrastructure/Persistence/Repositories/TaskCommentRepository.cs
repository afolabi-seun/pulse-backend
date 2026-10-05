using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class TaskCommentRepository : ITaskCommentRepository
{
    private readonly PulseDbContext _db;

    public TaskCommentRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<TaskComment>> GetByTaskAsync(Guid taskId, CancellationToken ct = default) =>
        await _db.TaskComments.Where(c => c.TaskId == taskId).OrderBy(c => c.CreatedAt).ToListAsync(ct);

    public async Task<TaskComment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.TaskComments.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task AddAsync(TaskComment comment, CancellationToken ct = default) =>
        await _db.TaskComments.AddAsync(comment, ct);

    public Task DeleteAsync(TaskComment comment, CancellationToken ct = default)
    {
        _db.TaskComments.Remove(comment);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
