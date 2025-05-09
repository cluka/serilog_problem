using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace WebApplication1;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception == null || httpContext == null) return false;

        if (httpContext.Response.HasStarted)
        {
            logger.LogResponseStarted();
            return false;
        }

        logger.LogUnhandledException(exception.Message, exception);

        var status = exception switch
        {
            ArgumentException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        httpContext.Response.StatusCode = status;

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = "An error occurred",
            Type = exception.GetType().Name,
            Detail = exception.Message
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            Exception = exception,
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }
}

public static partial class GlobalExceptionHandlerLogger
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception {message}")]
    public static partial void LogUnhandledException(this ILogger logger, string message, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "The response has already started. Unable to handle the exception.")]
    public static partial void LogResponseStarted(this ILogger logger);
}