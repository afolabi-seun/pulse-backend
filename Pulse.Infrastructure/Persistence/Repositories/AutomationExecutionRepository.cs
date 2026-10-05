using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Automations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AutomationExecutionRepository : IAutomationExecutionRepository
{
    private readonly PulseDbContext _db;

    public AutomationExecutionRepository(PulseDbContext db) => _db = db;

    public async Task<AutomationExecution?> GetLatestAsync(Guid automationRuleId, Guid taskId, CancellationToken ct = default) =>
        await _db.AutomationExecutions
            .Where(e => e.AutomationRuleId == automationRuleId && e.TaskId == taskId)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task AddAsync(AutomationExecution execution, CancellationToken ct = default) =>
        await _db.AutomationExecutions.AddAsync(execution, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
