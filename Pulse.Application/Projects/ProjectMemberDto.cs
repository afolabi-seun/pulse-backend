namespace Pulse.Application.Projects;

public record ProjectMemberDto(
    Guid   EngineerId,
    string Name,
    string Email,
    string Role,
    DateTime AddedAt);
