using Pulse.Application.Projects;
using Pulse.Domain.Projects;

namespace Pulse.Application.Common.Interfaces;

/// <summary>Weekly throughput data point — delivered story points for a calendar week.</summary>
public record WeeklyThroughputPoint(DateOnly WeekOf, int PointsDelivered);

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>The caller's own personal-tasks project, or null if they haven't made one yet.</summary>
    Task<Project?> GetPersonalProjectAsync(Guid ownerId, CancellationToken ct = default);
    /// <summary>Which of <paramref name="projectIds"/> are personal-tasks projects (see Project.PersonalOwnerId).
    /// Lets reports that name projects keep a person's private to-dos out of view.</summary>
    Task<IReadOnlySet<Guid>> GetPersonalProjectIdsAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListActiveAsync(CancellationToken ct = default);
    /// <summary>Batch id-to-name lookup for denormalizing a project name onto another DTO
    /// (e.g. a task list row) without loading full Project entities.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    /// <summary>Batch id-to-code lookup, the Code analogue of <see cref="GetNamesByIdsAsync"/> — for
    /// denormalizing each task's display key (Code-TaskNumber) onto a list row without an N+1 lookup.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetCodesByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    /// <summary>Every project code currently in use, across every status (active, paused, archived) —
    /// archived projects still reserve their code, so it isn't handed out again. Used by
    /// <see cref="Pulse.Application.Projects.ProjectCodeGenerator"/>'s callers to dedupe.</summary>
    Task<IReadOnlySet<string>> GetAllCodesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MyProjectDto>> ListMyProjectsAsync(Guid userId, CancellationToken ct = default);
    Task AddAsync(Project project, CancellationToken ct = default);
    void Remove(Project project);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Returns delivered story points grouped by week (Monday) for the 6-week rolling window ending today.</summary>
    Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputAsync(Guid projectId, CancellationToken ct = default);

    // ── Project membership ───────────────────────────────────────────────────
    Task<IReadOnlyList<ProjectMemberDto>> ListMembersAsync(Guid projectId, CancellationToken ct = default);
    Task<bool> IsMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default);

    /// <summary>All project IDs the given engineer is an explicit member of — one bulk lookup for
    /// callers that need to check membership across many projects (e.g. a project list's per-row
    /// access flag) without an IsMemberAsync round-trip per project.</summary>
    Task<IReadOnlySet<Guid>> GetMemberProjectIdsAsync(Guid engineerId, CancellationToken ct = default);
    Task AddMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid projectId, Guid engineerId, CancellationToken ct = default);
}
