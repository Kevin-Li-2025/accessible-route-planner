using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AccessCity.API.Filters;

/// <summary>
/// Returns 400 Bad Request with the exception message when an ArgumentException (or derivative) is thrown.
/// </summary>
public class BadRequestExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ArgumentException)
            return;

        context.Result = new ObjectResult(new { error = context.Exception.Message })
        {
            StatusCode = (int)HttpStatusCode.BadRequest,
        };
        context.ExceptionHandled = true;
    }
}
