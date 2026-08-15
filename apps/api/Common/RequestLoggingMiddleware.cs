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
        // UseExceptionHandler() is registered ahead of this middleware (see
        // Program.cs), so it's the outer layer: an exception thrown further
        // down the pipeline propagates *through* this middleware's `await
        // next(context)` before the exception handler ever writes the real
        // 500 response. Without catching it here, both the log line below and
        // the HttpErrors metric would report whatever context.Response.StatusCode
        // still defaults to at that point (200) instead of what the client
        // actually receives — this catch is what makes them accurate.
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
