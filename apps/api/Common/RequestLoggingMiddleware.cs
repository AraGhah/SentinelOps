using System.Diagnostics;

namespace SentinelOps.Api.Common;

// Structured request logging without a third-party sink: one log entry per
// request, correlation id = the ASP.NET Core TraceIdentifier already attached
// to every request, carried as a logger scope so any log line written further
// down the pipeline (including by GlobalExceptionHandler) picks it up too.
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.TraceIdentifier,
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "{Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                context.Request.Method, context.Request.Path, context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
