using CitibikeIngest.Api.Models;

namespace CitibikeIngest.Api.Infrastructure;

/// <summary>House error contract: correct status code + short plain-text body. Details go to the log only.</summary>
public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, message) = ex switch
            {
                NotFoundException => (StatusCodes.Status404NotFound, ex.Message),
                ConflictException => (StatusCodes.Status409Conflict, ex.Message),
                ValidationException => (StatusCodes.Status400BadRequest, ex.Message),
                UpstreamException => (StatusCodes.Status502BadGateway, ex.Message),
                NotConfiguredException => (StatusCodes.Status503ServiceUnavailable, ex.Message),
                OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "Request cancelled"),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error; see the backend log"),
            };

            if (status >= 500 && ex is not UpstreamException and not NotConfiguredException) // the manager already logged the upstream failure
            {
                logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            }
            else
            {
                logger.LogInformation("{Method} {Path} -> {Status}: {Message}", context.Request.Method, context.Request.Path, status, message);
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(message);
        }
    }
}
