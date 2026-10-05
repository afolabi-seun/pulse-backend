namespace Pulse.Application.Common;

public class ServiceResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ServiceResult<T> Ok(T data) =>
        new() { IsSuccess = true, Data = data };

    public static ServiceResult<T> Fail(string code, string message) =>
        new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
}
