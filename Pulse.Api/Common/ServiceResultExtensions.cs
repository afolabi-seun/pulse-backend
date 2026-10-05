using Pulse.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Common;

/// <summary>Maps a <see cref="ServiceResult{T}"/> to an <see cref="IActionResult"/> using the standard response envelope.</summary>
public static class ServiceResultExtensions
{
    public static IActionResult ToActionResult<T>(this ServiceResult<T> result) =>
        result.IsSuccess
            ? new OkObjectResult(ApiResponse<T>.Success(result.Data!))
            : result.ErrorCode switch
            {
                "NOT_FOUND"               => new NotFoundObjectResult(Failure<T>(result)),
                "FORBIDDEN"               => new ObjectResult(Failure<T>(result)) { StatusCode = 403 },
                "UNAUTHORIZED"            => new ObjectResult(Failure<T>(result)) { StatusCode = 401 },
                "ACCOUNT_LOCKED"          => new ObjectResult(Failure<T>(result)) { StatusCode = 401 },
                "CONFLICT"                => new ConflictObjectResult(Failure<T>(result)),
                "BUSINESS_RULE_VIOLATION" => new UnprocessableEntityObjectResult(Failure<T>(result)),
                "TASK_MISSING_POINTS"     => new UnprocessableEntityObjectResult(Failure<T>(result)),
                _                         => new BadRequestObjectResult(Failure<T>(result))
            };

    /// <summary>Variant for 204 No Content responses on DELETE endpoints.</summary>
    public static IActionResult ToNoContentResult<T>(this ServiceResult<T> result) =>
        result.IsSuccess ? new NoContentResult() : result.ToActionResult();

    /// <summary>Variant for 201 Created responses on POST endpoints that create a resource with a Location header.</summary>
    public static IActionResult ToCreatedResult<T>(this ServiceResult<T> result, string routeName, object? routeValues) =>
        result.IsSuccess
            ? new CreatedAtRouteResult(routeName, routeValues, ApiResponse<T>.Success(result.Data!))
            : result.ToActionResult();

    /// <summary>Variant for 201 Created responses where no Location header is needed (e.g. feedback, vitals).</summary>
    public static IActionResult ToCreatedResult<T>(this ServiceResult<T> result) =>
        result.IsSuccess
            ? new ObjectResult(ApiResponse<T>.Success(result.Data!)) { StatusCode = StatusCodes.Status201Created }
            : result.ToActionResult();

    private static ApiResponse<T> Failure<T>(ServiceResult<T> result) =>
        ApiResponse<T>.Failure(result.ErrorCode!, result.ErrorMessage!);
}
