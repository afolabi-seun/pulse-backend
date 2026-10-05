using System.Text;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class EngineerRepository : IEngineerRepository
{
    private readonly PulseDbContext _db;

    public EngineerRepository(PulseDbContext db) => _db = db;

    public async Task<Engineer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Engineers.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Engineer>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await _db.Engineers.Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    public async Task<Engineer?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        await _db.Engineers.FirstOrDefaultAsync(e => e.Email == email, ct);

    public async Task<IReadOnlyList<string>> GetExistingEmailsAsync(IReadOnlyList<string> emails, CancellationToken ct = default) =>
        await _db.Engineers.Where(e => emails.Contains(e.Email)).Select(e => e.Email).ToListAsync(ct);

    public async Task<IReadOnlyList<Engineer>> ListActiveAsync(CancellationToken ct = default) =>
        await _db.Engineers.Where(e => e.IsActive).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, int>> CountByTeamAsync(CancellationToken ct = default) =>
        await _db.Engineers
            .Where(e => e.TeamId != null)
            .GroupBy(e => e.TeamId!.Value)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TeamId, g => g.Count, ct);

    public async Task<IReadOnlyList<Engineer>> ListAllAsync(CancellationToken ct = default) =>
        await _db.Engineers.OrderBy(e => e.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<(Engineer Engineer, int ActiveTasks, int TotalPoints)>> ListWithWorkloadAsync(IReadOnlyList<string>? roles = null, Guid? teamId = null, CancellationToken ct = default)
    {
        var workload = await _db.Tasks
            .Where(t => t.AssigneeId != null
                && (t.Status == Pulse.Domain.Tasks.TaskStatus.Active || t.Status == Pulse.Domain.Tasks.TaskStatus.Blocked))
            .GroupBy(t => t.AssigneeId!.Value)
            .Select(g => new { AssigneeId = g.Key, Count = g.Count(), Points = g.Sum(t => t.Points) })
            .ToListAsync(ct);

        var workloadMap = workload.ToDictionary(w => w.AssigneeId);

        var q = _db.Engineers.AsQueryable();

        if (roles is { Count: > 0 })
        {
            var roleList = roles.ToList();
            q = q.Where(e => roleList.Contains(e.Role));
        }

        if (teamId.HasValue)
            q = q.Where(e => e.TeamId == teamId);

        var engineers = await q.OrderBy(e => e.Name).ToListAsync(ct);

        return engineers
            .Select(e => workloadMap.TryGetValue(e.Id, out var w)
                ? (e, w.Count, w.Points)
                : (e, 0, 0))
            .ToList();
    }

    public async Task<IReadOnlyList<Engineer>> ListAllPagedAsync(int limit, string? cursor, string? role = null, string? team = null, bool? isActive = null, IReadOnlyList<Guid>? callerTeamIds = null, bool? isQa = null, CancellationToken ct = default)
    {
        var offset = cursor is not null
            ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)))
            : 0;

        var q = _db.Engineers.AsQueryable();

        if (callerTeamIds is { Count: > 0 })
        {
            var teamIdList = callerTeamIds.ToList();
            q = q.Where(e => !e.TeamId.HasValue || teamIdList.Contains(e.TeamId.Value));
        }

        if (role is not null)
            q = q.Where(e => e.Role == role);

        if (!string.IsNullOrWhiteSpace(team))
            q = q.Where(e => e.Team != null && e.Team.Contains(team));

        if (isActive is not null)
            q = q.Where(e => e.IsActive == isActive);

        if (isQa is not null)
            q = q.Where(e => e.IsQa == isQa);

        return await q
            .OrderBy(e => e.Name).ThenBy(e => e.Id)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);
    }

    public async Task<(Engineer engineer, IReadOnlyList<PulseTask> activeTasks)> GetWithActiveTasksAsync(
        Guid id, CancellationToken ct = default)
    {
        var engineer = await _db.Engineers.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new InvalidOperationException($"Engineer {id} not found.");

        var tasks = await _db.Tasks
            .Where(t => t.AssigneeId == id
                && (t.Status == Pulse.Domain.Tasks.TaskStatus.Active || t.Status == Pulse.Domain.Tasks.TaskStatus.Blocked))
            .ToListAsync(ct);

        return (engineer, tasks);
    }

    public async Task<IReadOnlyList<Engineer>> ListByRoleAsync(string role, CancellationToken ct = default) =>
        await _db.Engineers.Where(e => e.Role == role && e.IsActive).ToListAsync(ct);

    public async Task<Engineer?> GetByResetTokenHashAsync(string tokenHash, CancellationToken ct = default) =>
        await _db.Engineers.FirstOrDefaultAsync(
            e => e.PasswordResetToken == tokenHash && e.PasswordResetTokenExpiresAt > DateTime.UtcNow, ct);

    public async Task<IReadOnlyList<Engineer>> SearchAsync(string q, int limit, CancellationToken ct = default) =>
        await _db.Engineers
            .Where(e => e.IsActive && (EF.Functions.ILike(e.Name, $"%{q}%") || EF.Functions.ILike(e.Email, $"%{q}%")))
            .OrderBy(e => e.Name)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<bool> AnyAsync(CancellationToken ct = default) =>
        await _db.Engineers.AnyAsync(ct);

    public async Task AddAsync(Engineer engineer, CancellationToken ct = default) =>
        await _db.Engineers.AddAsync(engineer, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_engineers_email" })
        {
            // Email is unique across organizations, but the caller's own lookups can't see other orgs.
            var pending = _db.ChangeTracker.Entries<Engineer>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified)
                .Select(e => e.Entity.Email)
                .ToList();
            throw new DuplicateEmailException(pending.Count == 1 ? pending[0] : null);
        }
    }
}
