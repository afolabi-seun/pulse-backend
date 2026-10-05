namespace Pulse.Application.Tasks;

/// <summary>The task-list columns that support explicit sorting — every one is a plain scalar
/// column on `tasks`, so no join is needed to order by it. Project/assignee sorting is
/// deliberately not supported: ordering by either meaningfully would require joining to resolve a
/// display name, and both are already filterable today (unlike these five).</summary>
public static class TaskSortFields
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "title", "status", "dueDate", "actualEndDate", "points",
    };

    public static readonly IReadOnlySet<string> Directions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "asc", "desc",
    };
}
