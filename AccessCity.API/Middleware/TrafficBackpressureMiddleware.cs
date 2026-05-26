using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AccessCity.API.Common;
using AccessCity.API.Configuration;
using AccessCity.API.Security;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Middleware;

/// <summary>
/// Path-aware backpressure for high-cost API surfaces. The built-in global limiter
/// protects total request volume; this middleware protects specific hot paths before
/// they can consume database connections, Kafka dispatch capacity, or route workers.
/// </summary>
public sealed class TrafficBackpressureMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IReadOnlyDictionary<string, PartitionedRateLimiter<HttpContext>> _limiters;

    public TrafficBackpressureMiddleware(
        RequestDelegate next,
        IOptions<AccessCityRateLimitingOptions> options)
    {
        _next = next;
        var value = options.Value;
        _limiters = new Dictionary<string, PartitionedRateLimiter<HttpContext>>(StringComparer.Ordinal)
        {
            [AccessCityRateLimitPolicies.Auth] = BuildLimiter(value.Auth),
            [AccessCityRateLimitPolicies.RoutingHeavy] = BuildLimiter(value.RoutingHeavy),
            [AccessCityRateLimitPolicies.RoutingPoll] = BuildLimiter(value.RoutingPoll),
            [AccessCityRateLimitPolicies.HotRead] = BuildLimiter(value.HotRead),
            [AccessCityRateLimitPolicies.Tile] = BuildLimiter(value.Tile),
            [AccessCityRateLimitPolicies.Write] = BuildLimiter(value.Write),
            [AccessCityRateLimitPolicies.Upload] = BuildLimiter(value.Upload),
            [AccessCityRateLimitPolicies.AiAssist] = BuildLimiter(value.AiAssist)
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var policyName = ResolvePolicy(context);
        if (policyName is null || !_limiters.TryGetValue(policyName, out var limiter))
        {
            await _next(context);
            return;
        }

        using var lease = await limiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        if (!lease.IsAcquired)
        {
            await RejectAsync(context, lease, policyName);
            return;
        }

        await _next(context);
    }

    private static PartitionedRateLimiter<HttpContext> BuildLimiter(RateLimitWindowOptions rule) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                ResolvePartitionKey(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, rule.PermitLimit),
                    Window = TimeSpan.FromSeconds(Math.Max(1, rule.WindowSeconds)),
                    SegmentsPerWindow = Math.Clamp(rule.SegmentsPerWindow, 1, Math.Max(1, rule.WindowSeconds)),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = Math.Max(0, rule.QueueLimit)
                }));

    private static async Task RejectAsync(HttpContext context, RateLimitLease lease, string policyName)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (!context.Response.HasStarted)
        {
            await context.Response.WriteAsJsonAsync(
                new ApiError(
                    "Too many requests.",
                    CorrelationId: context.TraceIdentifier,
                    Detail: $"The {policyName} traffic budget is exhausted. Retry after backoff or use async APIs for heavy work."),
                context.RequestAborted);
        }
    }

    private static string ResolvePartitionKey(HttpContext context)
    {
        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? context.User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(subject))
        {
            return "user:" + subject;
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static string? ResolvePolicy(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var method = context.Request.Method;

        if (path is "/health" or "/health/ready" || path.StartsWith("/hubs/", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("/api/v1/integrations/status", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith("/api/v1/auth/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.Auth;
        }

        if (path.StartsWith("/api/v1/routing/jobs/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.RoutingPoll;
        }

        if (HttpMethods.IsPost(method)
            && (path.StartsWith("/api/v1/routing/safe-path", StringComparison.Ordinal)
                || path.StartsWith("/api/v1/routing/safe-path/options", StringComparison.Ordinal)))
        {
            return AccessCityRateLimitPolicies.RoutingHeavy;
        }

        if (path.StartsWith("/api/v1/routing/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.HotRead;
        }

        if (path.StartsWith("/api/v1/tiles/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.Tile;
        }

        if (path.StartsWith("/api/v1/ai-assist/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.AiAssist;
        }

        if (path.Contains("/photo", StringComparison.Ordinal))
        {
            return HttpMethods.IsPost(method) ? AccessCityRateLimitPolicies.Upload : AccessCityRateLimitPolicies.HotRead;
        }

        if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
        {
            return AccessCityRateLimitPolicies.Write;
        }

        if (path.StartsWith("/api/v1/dashboard/", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/hazards", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/spatial/", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/geocoding/", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/safe-haven/", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/offlinemap/", StringComparison.Ordinal)
            || path.StartsWith("/api/v1/account/", StringComparison.Ordinal))
        {
            return AccessCityRateLimitPolicies.HotRead;
        }

        return null;
    }
}
