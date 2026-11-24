using Microsoft.AspNetCore.Diagnostics;
using ShowingBooking.Api.Application;
using ShowingBooking.Api.Domain;

namespace ShowingBooking.Api;

/// <summary>Single place that turns exceptions into 400 / 404 / 409 ProblemDetails responses.</summary>
public class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        IResult? result = exception switch
        {
            RequestValidationException v => Results.ValidationProblem(v.Errors),
            BadHttpRequestException b => Results.Problem(title: "Bad request", detail: b.Message, statusCode: StatusCodes.Status400BadRequest),
            NotFoundException n => Results.Problem(title: "Not found", detail: n.Message, statusCode: StatusCodes.Status404NotFound),
            ConflictException or DomainRuleException => Results.Problem(title: "Conflict", detail: exception.Message, statusCode: StatusCodes.Status409Conflict),
            _ => null
        };

        if (result is null)
        {
            logger.LogError(exception, "Unhandled exception");
            return false; // falls through to the default 500 ProblemDetails
        }

        await result.ExecuteAsync(context);
        return true;
    }
}
