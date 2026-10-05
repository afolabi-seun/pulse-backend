namespace Pulse.Application.Search;

public record TaskHitDto(Guid Id, string Title, int Points, string Status, string? TaskKey = null);
public record EngineerHitDto(Guid Id, string Name, string Email, string Role);
public record SearchResultDto(IReadOnlyList<TaskHitDto> Tasks, IReadOnlyList<EngineerHitDto> Engineers);
