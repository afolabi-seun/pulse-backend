using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface IEngineerRepository
{
    Task<Engineer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Engineer>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task<Engineer?> GetByEmailAsync(string email, CancellationToken ct = default);
    /// <summary>Returns the subset of the given emails that already have an account, regardless of
    /// active/inactive status. Used to check a batch of candidate emails before inviting people.</summary>
    Task<IReadOnlyList<string>> GetExistingEmailsAsync(IReadOnlyList<string> emails, CancellationToken ct = default);
    Task<IReadOnlyList<Engineer>> ListActiveAsync(CancellationToken ct = default);
    /// <summary>Returns the number of engineers (active and inactive) per team.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountByTeamAsync(CancellationToken ct = default);
    /// <summary>Returns all engineers including inactive accounts. Used by the admin users management surface.</summary>
    Task<IReadOnlyList<Engineer>> ListAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(Engineer Engineer, int ActiveTasks, int TotalPoints)>> ListWithWorkloadAsync(IReadOnlyList<string>? roles = null, Guid? teamId = null, CancellationToken ct = default);
    Task<IReadOnlyList<Engineer>> ListAllPagedAsync(int limit, string? cursor, string? role = null, string? team = null, bool? isActive = null, IReadOnlyList<Guid>? callerTeamIds = null, bool? isQa = null, CancellationToken ct = default);
    Task<(Engineer engineer, IReadOnlyList<PulseTask> activeTasks)> GetWithActiveTasksAsync(Guid id, CancellationToken ct = default);
    /// <summary>
    /// Returns the engineer whose hashed password-reset token matches <paramref name="tokenHash"/>
    /// and whose reset token has not yet expired. Returns null for unknown or expired tokens.
    /// </summary>
    Task<IReadOnlyList<Engineer>> ListByRoleAsync(string role, CancellationToken ct = default);
    Task<Engineer?> GetByResetTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<Engineer>> SearchAsync(string q, int limit, CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task AddAsync(Engineer engineer, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
