using System.Net;
using AccessCity.API.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AccessCity.API.Filters;

/// <summary>
/// Returns 503 Service Unavailable with correlation id when Overpass (or other external hazard data) fails.
/// </summary>
public class OverpassExceptionFilter : IExceptionFilter
{
    private readonly ILogger<OverpassExceptionFilter> _logger;

    public OverpassExceptionFilter(ILogger<OverpassExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not OverpassServiceException)
            return;

        var correlationId = context.HttpContext.TraceIdentifier;
        _logger.LogWarning(context.Exception,
            "Overpass failure mapped to 503. CorrelationId: {CorrelationId}",
            correlationId);

        context.Result = new ObjectResult(new
        {
            error = "Hazard data service temporarily unavailable.",
            correlationId,
        })
        {
            StatusCode = (int)HttpStatusCode.ServiceUnavailable,
        };
        context.ExceptionHandled = true;
    }
}
