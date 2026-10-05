using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ProjectRepository : IProjectRepository
{
    private readonly PulseDbContext _db;

    public ProjectRepository(PulseDbContext db) => _db = db;

    public async Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Project?> GetPersonalProjectAsync(Guid ownerId, CancellationToken ct = default) =>
        await _db.Projects.FirstOrDefaultAsync(p => p.PersonalOwnerId == ownerId, ct);

    public async Task<IReadOnlySet<Guid>> GetPersonalProjectIdsAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct = default) =>
        projectIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Projects.Where(p => projectIds.Contains(p.Id) && p.PersonalOwnerId != null).Select(p => p.Id).ToListAsync(ct)).ToHashSet();

    // Personal projects (one per person, for their own to-dos) are excluded from every enumeration
    // below — they are not part of the org's project portfolio and never appear in a list, picker or
    // report. They are only reached directly, by id, through their owner's own tasks.
    //
    // "Active" here means "not archived" — a paused project is still ongoing and stays visible,
    // it just carries the Paused status; only Archived is actually excluded.
    public async Task<IReadOnlyList<Project>> ListActiveAsync(CancellationToken ct = default) =>
        await _db.Projects.Where(p => p.Status != ProjectStatus.Archived && p.PersonalOwnerId == null).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await _db.Projects.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    public async Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await _db.Projects.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);

    public async Task<IReadOnlySet<string>> GetAllCodesAsync(CancellationToken ct = default) =>
        (await _db.Projects.Select(p => p.Code).ToListAsync(ct)).ToHashSet();

    public async Task AddAsync(Project project, CancellationToken ct = default) =>
        await _db.Projects.AddAsync(project, ct);

    public void Remove(Project project) => _db.Projects.Remove(project);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<MyProjectDto>> ListMyProjectsAsync(Guid userId, CancellationToken ct = default)
    {
        // Collect all project IDs the engineer has a stake in: assigned tasks, explicit membership, or owning team.
        var assignedIds = await _db.Tasks
            .Where(t => t.AssigneeId == userId)
            .Select(t => t.ProjectId)
            .Distinct()
            .ToListAsync(ct);

        var memberIds = await _db.ProjectMembers
            .Where(m => m.EngineerId == userId)
            .Select(m => m.ProjectId)
            .ToListAsync(ct);

        var engineer = await _db.Engineers.FindAsync(new object[] { userId }, ct);
        var teamIds = engineer?.TeamId is Guid teamId
            ? await _db.Projects.Where(p => p.Status != ProjectStatus.Archived && p.PersonalOwnerId == null && p.OwnerTeamId == teamId).Select(p => p.Id).ToListAsync(ct)
            : [];

        var allIds = assignedIds.Union(memberIds).Union(teamIds).Distinct().ToList();

        if (allIds.Count == 0)
            return [];

        // Return project-wide task counts so engineers see the health of their project, not just their slice.
        // Order before projecting into the record — EF Core cannot translate an OrderBy over a
        // constructed MyProjectDto (it tried to re-run the sub-query projection to sort by it).
        return await _db.Projects
            .Where(p => p.Status != ProjectStatus.Archived && p.PersonalOwnerId == null && allIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => new MyProjectDto(
                p.Id,
                p.Name,
                p.Description,
                _db.Tasks.Count(t => t.ProjectId == p.Id && t.Status != Pulse.Domain.Tasks.TaskStatus.Done),
                _db.Tasks.Count(t => t.ProjectId == p.Id && t.Status == Pulse.Domain.Tasks.TaskStatus.Blocked)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputAsync(Guid projectId, CancellationToken ct = default)
    {
        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        // Project-wide throughput — exclude QA sub-tasks so a task that went through QA isn't
        // counted as two separate pieces of scope.
        var completions = await _db.TaskHistory
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h, t })
            .Where(x => x.t.ProjectId == projectId
                     && x.t.ParentTaskId == null
                     && x.h.Field == "status"
                     && x.h.NewValue == "Done"
                     && x.h.ChangedAt >= sixWeeksAgo)
            .Select(x => new { x.t.Points, x.h.ChangedAt })
            .ToListAsync(ct);

        return completions
            .GroupBy(x =>
            {
                var dow = (int)x.ChangedAt.DayOfWeek;
                return DateOnly.FromDateTime(x.ChangedAt.AddDays(dow == 0 ? -6 : 1 - dow));
            })
            .Select(g => new WeeklyThroughputPoint(g.Key, g.Sum(x => x.Points)))
            .OrderBy(p => p.WeekOf)
            .ToList();
    }

    // ── Project membership ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProjectMemberDto>> ListMembersAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.ProjectMembers
            .Where(m => m.ProjectId == projectId)
            .Join(_db.Engineers, m => m.EngineerId, e => e.Id,
                  (m, e) => new { e.Id, e.Name, e.Email, e.Role, m.AddedAt })
            .OrderBy(x => x.Name)
            .Select(x => new ProjectMemberDto(x.Id, x.Name, x.Email, x.Role, x.AddedAt))
            .ToListAsync(ct);

    public async Task<bool> IsMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default) =>
        await _db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.EngineerId == engineerId, ct);

    public async Task<IReadOnlySet<Guid>> GetMemberProjectIdsAsync(Guid engineerId, CancellationToken ct = default) =>
        (await _db.ProjectMembers
            .Where(m => m.EngineerId == engineerId)
            .Select(m => m.ProjectId)
            .ToListAsync(ct))
        .ToHashSet();

    public async Task AddMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default)
    {
        if (!await IsMemberAsync(projectId, engineerId, ct))
            await _db.ProjectMembers.AddAsync(ProjectMember.Create(projectId, engineerId), ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default)
    {
        var member = await _db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.EngineerId == engineerId, ct);
        if (member is not null)
        {
            _db.ProjectMembers.Remove(member);
            await _db.SaveChangesAsync(ct);
        }
    }
}
