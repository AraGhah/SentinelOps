using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.IncidentCreation;

// Consumes only direct SendMessage hand-offs from the deduplication worker
// (IncidentCreationRequest, not an EventBridge event — see that record's
// comment). Creates the Incident, links the triggering Alert, publishes
// `incident.created`.
public class Function
{
    public const string WorkerName = "incident-creation";

    // Kept small — the dedup worker's FingerprintTtl (48h) covers how long a
    // fingerprint stays alive between alerts; this only needs to survive long
    // enough for a redelivered "Pending" lookup to find the real incident id.
    private static readonly TimeSpan FingerprintTtl = TimeSpan.FromHours(48);

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly IFingerprintStore _fingerprintStore;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment(),
        DynamoDbFingerprintStore.FromEnvironment())
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher, IFingerprintStore fingerprintStore)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _fingerprintStore = fingerprintStore;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var request = JsonSerializer.Deserialize<IncidentCreationRequest>(record.Body, EventJson.Options)
            ?? throw new InvalidOperationException("SQS message body is not a valid IncidentCreationRequest.");

        await using var db = WorkerDbContextFactory.Create(_connectionString, request.OrganizationId);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, request.EventId, CancellationToken.None);
        if (claimState == ClaimState.AlreadyCompleted)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                request.EventId, request.OrganizationId, request.CorrelationId);
            return;
        }

        if (claimState == ClaimState.PendingCompletion)
        {
            // A prior attempt already created the Incident row (or determined
            // one wasn't needed) and committed that — do NOT run the creation
            // logic again, which would create a second Incident for the same
            // alert. Just replay the captured publish and finish.
            WorkerLog.Info(context, WorkerName, "Retrying outbound publish for a previously-claimed event.",
                request.EventId, request.OrganizationId, request.CorrelationId);
            var pending = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, pending, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, request.EventId, CancellationToken.None);
            return;
        }

        var alert = await db.Alerts.Include(a => a.Integration).FirstOrDefaultAsync(a => a.Id == request.AlertId);
        if (alert is null)
        {
            WorkerLog.Warn(context, WorkerName, "Alert no longer exists, skipping incident creation.",
                request.EventId, request.OrganizationId, request.CorrelationId, new { alertId = request.AlertId });
            // No outbound publish for this outcome — the claim is fully done
            // once this commits.
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            // Nobody will ever set a real IncidentId for this fingerprint now
            // — release it so a future alert with the same fingerprint isn't
            // stuck waiting out the TTL.
            await _fingerprintStore.ReleaseAsync(request.Fingerprint, CancellationToken.None);
            return;
        }

        // An alert can only ever cause one incident to be created for it — belt
        // and suspenders alongside the EventId-keyed IdempotencyGuard claim above,
        // in case this worker's queue ever redelivers under a *different* EventId
        // (e.g. an operator manually replaying the dedup worker's send).
        if (alert.IncidentId is not null)
        {
            WorkerLog.Info(context, WorkerName, "Alert already has an incident, skipping.",
                request.EventId, request.OrganizationId, request.CorrelationId, new { incidentId = alert.IncidentId });
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            await _fingerprintStore.SetIncidentIdAsync(request.Fingerprint, alert.IncidentId.Value, FingerprintTtl, CancellationToken.None);
            return;
        }

        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            OrganizationId = request.OrganizationId,
            Title = alert.Title,
            Description = alert.Description,
            Severity = alert.Severity,
            ServiceId = alert.Integration?.ServiceId,
            Status = IncidentStatus.Triggered,
            AlertCount = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        db.Incidents.Add(incident);
        alert.IncidentId = incident.Id;

        var items = new List<OutboxItem>
        {
            OutboxItem.EventBridge(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated,
                new IncidentCreatedDetail(
                    Guid.NewGuid(), request.OrganizationId, request.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, alert.Id, incident.ServiceId, incident.Title, SeverityMapping.ToEventSeverity(incident.Severity))),
        };
        claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);

        await db.SaveChangesAsync(CancellationToken.None);

        // The fingerprint store isn't part of the outbox/PendingOutboxJson
        // replay: it's DynamoDB, not an at-least-once "publish" that dropped
        // messages when unretried, and SetIncidentIdAsync is itself
        // idempotent (setting the same value twice is a no-op), so it's safe
        // to just call it again here on every attempt.
        await _fingerprintStore.SetIncidentIdAsync(request.Fingerprint, incident.Id, FingerprintTtl, CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Incident created.",
            request.EventId, request.OrganizationId, request.CorrelationId, new { incidentId = incident.Id });
        WorkerMetrics.Emit("IncidentsCreated", 1, dimensions: new Dictionary<string, string> { ["Worker"] = WorkerName });

        await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, items, CancellationToken.None);
        await IdempotencyGuard.CompleteAsync(db, WorkerName, request.EventId, CancellationToken.None);
    }
}

// IncidentSeverity (apps/api/Domain/Incident.cs) and Severity (SentinelOps.Events)
// are declared independently on purpose (see Severity.cs's comment), so a raw
// cast between them would silently miscast if either enum's members/order ever
// drifted. This makes the mapping explicit — a missed case fails loudly instead.
internal static class SeverityMapping
{
    public static Severity ToEventSeverity(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Critical => Severity.Critical,
        IncidentSeverity.High => Severity.High,
        IncidentSeverity.Medium => Severity.Medium,
        IncidentSeverity.Low => Severity.Low,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unmapped IncidentSeverity value."),
    };
}
