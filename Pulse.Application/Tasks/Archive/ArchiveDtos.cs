namespace Pulse.Application.Tasks.Archive;

/// <summary>One task in an archive preview or summary.</summary>
public record ArchiveTaskLine(Guid Id, string Key, string Title, string Status, string? AssigneeName, string ProjectName);

public record ArchiveProjectSummary(
    Guid ProjectId, string ProjectName, int Total, int Backlog, int Active, int Blocked, int InQa, int Paused, int Done);

/// <summary>What archiving the tasks created before <see cref="BaselineStart"/> does (or did, when <see cref="DryRun"/> is false).</summary>
public record ArchiveTasksResult(
    bool DryRun,
    DateOnly BaselineStart,
    int ToArchive,
    int Open,
    int Done,
    int KeptBecauseTouched,
    IReadOnlyList<ArchiveProjectSummary> Projects,
    /// <summary>The not-done tasks being archived, so the people affected can be told (capped; see <see cref="OpenTasksTruncated"/>).</summary>
    IReadOnlyList<ArchiveTaskLine> OpenTasks,
    bool OpenTasksTruncated,
    /// <summary>Tasks created before the baseline that were left alone because something happened to them since (capped).</summary>
    IReadOnlyList<ArchiveTaskLine> KeptTasks,
    bool KeptTasksTruncated);

public record ArchivedTaskDto(
    Guid Id, string Key, string Title, string Status, Guid ProjectId, string ProjectName, string? AssigneeName,
    bool IsQaTask, DateTime ArchivedAt, string? ArchivedByName, string? Reason);

public record ArchivedTaskPage(IReadOnlyList<ArchivedTaskDto> Items, int Total, int Page, int PageSize);
