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
            // Incident already created (or determined unnecessary) and committed.
            // Don't rerun creation logic — replay the captured publish instead.
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
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            // Release the fingerprint so a future alert with the same fingerprint
            // doesn't wait out the TTL for an IncidentId that will never be set.
            await _fingerprintStore.ReleaseAsync(request.Fingerprint, CancellationToken.None);
            return;
        }

        // Extra guard beyond the EventId-keyed claim above, in case this queue ever
        // redelivers under a different EventId (e.g. a manual replay).
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

        // Not part of the outbox replay: SetIncidentIdAsync is idempotent, so it's
        // safe to call again on every attempt rather than tracking it in the outbox.
        await _fingerprintStore.SetIncidentIdAsync(request.Fingerprint, incident.Id, FingerprintTtl, CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Incident created.",
            request.EventId, request.OrganizationId, request.CorrelationId, new { incidentId = incident.Id });
        WorkerMetrics.Emit("IncidentsCreated", 1, dimensions: new Dictionary<string, string> { ["Worker"] = WorkerName });

        await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, items, CancellationToken.None);
        await IdempotencyGuard.CompleteAsync(db, WorkerName, request.EventId, CancellationToken.None);
    }
}

// IncidentSeverity and Severity are separate enums with independent ordering;
// an explicit switch avoids a raw cast silently miscasting if either drifts.
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
