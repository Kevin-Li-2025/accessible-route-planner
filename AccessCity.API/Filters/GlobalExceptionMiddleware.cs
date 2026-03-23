using System.Net;
using System.Text.Json;
using AccessCity.API.Common;

namespace AccessCity.API.Filters;

/// <summary>
/// Catches any unhandled exception, logs it with a correlation ID, and returns a structured
/// <see cref="ApiError"/> so clients never receive a raw stack trace.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — no response needed.
            context.Response.StatusCode = 499; // nginx-style "Client Closed Request"
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var error = new ApiError(
                Error: "An unexpected error occurred.",
                CorrelationId: correlationId);

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(error, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
    }
}
