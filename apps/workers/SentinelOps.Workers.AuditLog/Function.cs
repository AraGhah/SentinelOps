using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.AuditLog;

// Wildcard consumer, same as the analytics worker — writes straight to the
// existing AuditLog table (apps/api/Common/IAuditLogger.cs) rather than going
// through IAuditLogger, which is HTTP-request-scoped and assumes an
// authenticated principal; these are system-originated audit entries with no
// HTTP context and often no actor at all.
public class Function
{
    public const string WorkerName = "audit-log";

    private readonly string _connectionString;

    public Function() : this(
        Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException("CONNECTION_STRING environment variable is not set."))
    {
    }

    public Function(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        foreach (var record in sqsEvent.Records)
        {
            await HandleAsync(record, context);
        }
    }

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var common = envelope.DeserializeDetail<CommonEventFields>();

        await using var db = WorkerDbContextFactory.CreateUnscoped(_connectionString);

        if (!await IdempotencyGuard.TryClaimAsync(db, WorkerName, common.EventId, CancellationToken.None))
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                common.EventId, common.OrganizationId, common.CorrelationId);
            return;
        }

        db.AuditLogs.Add(new SentinelOps.Api.Domain.AuditLog
        {
            Id = Guid.NewGuid(),
            OrganizationId = common.OrganizationId,
            ActorUserId = null,
            Action = envelope.DetailType,
            EntityType = "Event",
            EntityId = common.EventId,
            Details = record.Body,
            CreatedAtUtc = common.OccurredAtUtc,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Audit log entry recorded.",
            common.EventId, common.OrganizationId, common.CorrelationId, new { detailType = envelope.DetailType });
    }
}

// Same minimal shape as the analytics worker needs — duplicated rather than
// shared to avoid a cross-worker-project dependency for four fields.
public record CommonEventFields(Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
