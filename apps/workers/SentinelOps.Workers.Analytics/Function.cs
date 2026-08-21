using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Analytics;

// Wildcard consumer — its EventBridge rule matches every detail-type on the bus.
// Doesn't need to know each event's specific shape, only the fields every
// IEventDetail carries, so it writes one AnalyticsEvent row per event without a
// switch over detail-type.
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

        // No outbound publish here — the row write below is the entire unit
        // of work, so the claim is Completed in the same commit.
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

// The subset of IEventDetail's fields needed here — deliberately not `IEventDetail`
// itself, since that's an interface and can't be deserialized directly.
public record CommonEventFields(Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc);
