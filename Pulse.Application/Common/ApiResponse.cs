namespace Pulse.Application.Common;

/// <summary>The standard response envelope returned by every API endpoint.</summary>
public class ApiResponse<T>
{
    public string Status { get; init; } = string.Empty;
    public T? Data { get; init; }
    public ApiError? Error { get; init; }
    public ApiMeta Meta { get; init; } = ApiMeta.Now();

    /// <summary>Creates a success envelope wrapping <paramref name="data"/>.</summary>
    public static ApiResponse<T> Success(T data) =>
        new() { Status = "success", Data = data };

    /// <summary>Creates an error envelope with a stable machine-readable <paramref name="code"/>.</summary>
    public static ApiResponse<T> Failure(string code, string message) =>
        new() { Status = "error", Error = new ApiError(code, message) };
}

/// <summary>Error payload included in all non-success responses.</summary>
/// <param name="Code">Stable uppercase identifier the frontend can branch on (e.g. NOT_FOUND).</param>
/// <param name="Message">Human-readable description — never relied on for logic.</param>
public record ApiError(string Code, string Message)
{
    /// <summary>Per-field validation messages, present only on 400 responses.</summary>
    public Dictionary<string, string[]>? Errors { get; init; }
}

/// <summary>Request metadata attached to every response.</summary>
public record ApiMeta(string RequestId, DateTimeOffset Timestamp)
{
    public static ApiMeta Now() =>
        new(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
}
