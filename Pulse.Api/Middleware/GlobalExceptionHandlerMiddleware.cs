using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Domain.Common;

namespace Pulse.Api.Middleware;

public class GlobalExceptionHandlerMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Domain rule violation on {Method} {Path} for {User}",
                context.Request.Method,
                context.Request.Path,
                context.User.Identity?.Name ?? "anonymous");

            await WriteError(context, HttpStatusCode.UnprocessableEntity, "DOMAIN_ERROR", ex.Message,
                detail: _env.IsDevelopment() ? ex.ToString() : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path} for {User}",
                context.Request.Method,
                context.Request.Path,
                context.User.Identity?.Name ?? "anonymous");

            await WriteError(context, HttpStatusCode.InternalServerError, "INTERNAL_ERROR",
                message: _env.IsDevelopment() ? ex.Message : "An unexpected error occurred.",
                detail: _env.IsDevelopment() ? ex.ToString() : null);
        }
    }

    private static async Task WriteError(HttpContext context, HttpStatusCode status, string code, string message, string? detail = null)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";

        var body = JsonSerializer.Serialize(new
        {
            data    = (object?)null,
            error   = new { code, message, traceId = context.TraceIdentifier, detail },
            meta    = new { timestamp = DateTime.UtcNow }
        }, JsonOptions);

        await context.Response.WriteAsync(body);
    }
}
