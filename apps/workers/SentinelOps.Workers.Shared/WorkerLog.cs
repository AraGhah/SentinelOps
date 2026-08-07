using System.Text.Json;
using Amazon.Lambda.Core;

namespace SentinelOps.Workers.Shared;

// One structured JSON line per log entry (CorrelationId/EventId/OrganizationId
// always present), matching the structured-logging convention already used by
// apps/api/Common/RequestLoggingMiddleware. Lambda ships stdout to CloudWatch
// Logs automatically, so this is deliberately just a JSON-formatted Console.WriteLine.
public static class WorkerLog
{
    public static void Info(ILambdaContext context, string worker, string message,
        Guid? eventId = null, Guid? organizationId = null, Guid? correlationId = null, object? data = null) =>
        Write(context, "INFO", worker, message, eventId, organizationId, correlationId, data);

    public static void Warn(ILambdaContext context, string worker, string message,
        Guid? eventId = null, Guid? organizationId = null, Guid? correlationId = null, object? data = null) =>
        Write(context, "WARN", worker, message, eventId, organizationId, correlationId, data);

    public static void Error(ILambdaContext context, string worker, string message, Exception ex,
        Guid? eventId = null, Guid? organizationId = null, Guid? correlationId = null) =>
        Write(context, "ERROR", worker, message, eventId, organizationId, correlationId, new { ex.GetType().Name, ex.Message });

    private static void Write(
        ILambdaContext context, string level, string worker, string message,
        Guid? eventId, Guid? organizationId, Guid? correlationId, object? data)
    {
        var line = JsonSerializer.Serialize(new
        {
            level,
            worker,
            message,
            eventId,
            organizationId,
            correlationId,
            requestId = context.AwsRequestId,
            data,
        });
        context.Logger.LogLine(line);
    }
}
