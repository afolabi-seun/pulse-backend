namespace Pulse.Application.Projects;

public record MyProjectDto(
    Guid   Id,
    string Name,
    string? Description,
    int    ActiveTaskCount,
    int    BlockedTaskCount);
