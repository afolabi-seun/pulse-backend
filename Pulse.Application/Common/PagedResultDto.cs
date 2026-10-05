namespace Pulse.Application.Common;

public record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore);
