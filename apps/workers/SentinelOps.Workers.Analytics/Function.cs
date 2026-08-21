using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Analytics;

// Wildcard consumer — its EventBridge rule matches every detail-type on the bus.
// Only reads the common IEventDetail fields, so one AnalyticsEvent row is
// written per event with no switch over detail-type.
public class Function
{
    public const string WorkerName = "analytics";

    private readonly string _connectionString;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment())
    {
    }

    public Function(string connectionString)
    {
        _connectionString = connectionString;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var common = envelope.DeserializeDetail<CommonEventFields>();

        await using var db = WorkerDbContextFactory.CreateUnscoped(_connectionString);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, common.EventId, CancellationToken.None);
        if (claimState != ClaimState.Claimed)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                common.EventId, common.OrganizationId, common.CorrelationId);
            return;
        }

        // No outbound publish, so the claim completes in the same commit as the row write.
        claimRecord.Completed = true;

        db.AnalyticsEvents.Add(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = envelope.DetailType,
            OrganizationId = common.OrganizationId,
            CorrelationId = common.CorrelationId,
            SourceEventId = common.EventId,
            OccurredAtUtc = common.OccurredAtUtc,
        });

        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Analytics event recorded.",
            common.EventId, common.OrganizationId, common.CorrelationId, new { detailType = envelope.DetailType });
    }
}

// Subset of IEventDetail's fields; not IEventDetail itself since interfaces can't be deserialized.
public record CommonEventFields(Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
