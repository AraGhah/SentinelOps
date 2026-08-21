using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.AlertValidation;

// Consumes `alert.received` (own SQS queue + EventBridge rule). Re-checks the
// same timestamp bounds AlertIngestionService applies at ingestion time;
// belongs here since it runs after the alert has left the API process.
public class Function
{
    public const string WorkerName = "alert-validation";
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment())
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var detail = envelope.DeserializeDetail<AlertReceivedDetail>();
        EventSchemaValidator.Validate(detail);

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None);
        if (claimState != ClaimState.Claimed)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        // Outbound publish is a pure function of `detail`, so redelivery after a
        // crash just re-publishes the same conclusion instead of losing or duplicating it.
        claimRecord.Completed = true;
        await db.SaveChangesAsync(CancellationToken.None);

        var reason = Validate(detail);
        if (reason is not null)
        {
            WorkerLog.Warn(context, WorkerName, "Alert rejected.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { reason });

            await _eventPublisher.PublishAsync(EventSources.AlertValidationWorker, EventTypes.AlertRejected,
                new AlertRejectedDetail(Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow, detail.AlertId, reason),
                CancellationToken.None);
            return;
        }

        WorkerLog.Info(context, WorkerName, "Alert validated.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId);

        await _eventPublisher.PublishAsync(EventSources.AlertValidationWorker, EventTypes.AlertValidated,
            new AlertValidatedDetail(
                Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                detail.AlertId, detail.ExternalId, detail.Source, detail.Title, detail.Severity, detail.Environment, detail.Region),
            CancellationToken.None);
    }

    // Returns a rejection reason, or null if the alert is valid.
    public static string? Validate(AlertReceivedDetail detail)
    {
        var now = DateTimeOffset.UtcNow;
        if (detail.TimestampUtc > now + MaxFutureSkew) return "Alert timestamp is too far in the future.";
        if (detail.TimestampUtc < now - MaxAge) return "Alert timestamp is too old.";
        if (string.IsNullOrWhiteSpace(detail.ExternalId)) return "Alert is missing an external id.";
        if (string.IsNullOrWhiteSpace(detail.Title)) return "Alert is missing a title.";
        if (string.IsNullOrWhiteSpace(detail.Environment)) return "Alert is missing an environment.";
        return null;
    }
}
