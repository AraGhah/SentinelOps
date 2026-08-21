using System.Diagnostics;

namespace SentinelOps.Api.Common;

// One log entry per request; correlation id is TraceIdentifier, carried as a logger
// scope so downstream log lines (including GlobalExceptionHandler's) pick it up too.
public class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.TraceIdentifier,
        });

        var stopwatch = Stopwatch.StartNew();
        // UseExceptionHandler() sits outside this middleware, so an exception propagates
        // through before the real 500 is written. Catch here or the log/metric would
        // report the still-default 200 instead of what the client actually gets.
        var statusCode = 200;
        try
        {
            await next(context);
            statusCode = context.Response.StatusCode;
        }
        catch
        {
            statusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "{Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                context.Request.Method, context.Request.Path, statusCode, stopwatch.ElapsedMilliseconds);

            MetricsEmitter.Emit("RequestCount", 1);
            MetricsEmitter.Emit("Latency", stopwatch.Elapsed.TotalMilliseconds, "Milliseconds");
            if (statusCode >= 400)
            {
                MetricsEmitter.Emit("HttpErrors", 1,
                    dimensions: new Dictionary<string, string> { ["StatusCode"] = statusCode.ToString() });
            }
        }
    }
}
