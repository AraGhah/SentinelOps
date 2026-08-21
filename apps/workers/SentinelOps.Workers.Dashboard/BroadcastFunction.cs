using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Dashboard;

// Consumes the subset of events the dashboard cares about (incident created/
// updated/resolved, new alerts — see the EventBridge rule in
// infrastructure-stack.ts) and relays each one, as-is, to every connection
// registered for that event's organization. Untyped: routes on OrganizationId
// (present on every IEventDetail) without needing each event's specific shape;
// the frontend distinguishes by `type` (the EventBridge detail-type).
public class BroadcastFunction
{
    public const string WorkerName = "dashboard-broadcast";

    private readonly IConnectionStore _connectionStore;
    private readonly IConnectionBroadcaster _broadcaster;

    public BroadcastFunction() : this(DynamoDbConnectionStore.FromEnvironment(), ApiGatewayConnectionBroadcaster.FromEnvironment())
    {
    }

    public BroadcastFunction(IConnectionStore connectionStore, IConnectionBroadcaster broadcaster)
    {
        _connectionStore = connectionStore;
        _broadcaster = broadcaster;
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

        if (!envelope.Detail.TryGetProperty("organizationId", out var orgIdProperty)
            || !Guid.TryParse(orgIdProperty.GetString(), out var organizationId))
        {
            WorkerLog.Warn(context, WorkerName, "Event has no organizationId, skipping broadcast.",
                Guid.NewGuid(), Guid.Empty, Guid.Empty, new { detailType = envelope.DetailType });
            return;
        }

        var connectionIds = await _connectionStore.GetConnectionIdsAsync(organizationId, CancellationToken.None);
        if (connectionIds.Count == 0) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = envelope.DetailType, detail = envelope.Detail });

        foreach (var connectionId in connectionIds)
        {
            var delivered = await _broadcaster.TryPostAsync(connectionId, payload, CancellationToken.None);
            if (!delivered)
            {
                // Client disconnected without API Gateway invoking $disconnect; clean up now instead of waiting for TTL.
                await _connectionStore.RemoveAsync(connectionId, CancellationToken.None);
            }
        }
    }
}
