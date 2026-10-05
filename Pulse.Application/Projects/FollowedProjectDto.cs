namespace Pulse.Application.Projects;

public record FollowedProjectDto(
    Guid   ProjectId,
    string ProjectName,
    bool   IsFollowing,

    // Task summary
    int    ActiveTaskCount,
    int    BlockedTaskCount,
    int    DoneThisSprintCount,
    string? LastBlockerTitle,

    // Current sprint
    Guid?   ActiveSprintId,
    string? ActiveSprintName,
    DateOnly? ActiveSprintEndDate,

    DateTime FollowedSince);
