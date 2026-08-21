using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Deduplication;

// Consumes `alert.validated`. Fingerprints the alert (normalized title + error
// code + service + environment, see AlertFingerprint) and atomically touches
// that fingerprint's entry in DynamoDB. The first alert for a fingerprint hands
// off to the incident-creation worker's queue (see IncidentCreationRequest for
// why this isn't an EventBridge event); every later alert with the same
// fingerprint attaches to that incident instead of creating a new one.
public class Function
{
    public const string WorkerName = "deduplication";

    // How long a fingerprint stays "active" with no matching alerts before
    // DynamoDB expires it. A later alert past this window creates a fresh
    // incident rather than reopening/attaching to the old one — the fallback
    // for a stale fingerprint is the resolved-incident-reopen path below, not
    // an indefinitely-alive DynamoDB item.
    private static readonly TimeSpan FingerprintTtl = TimeSpan.FromHours(48);

    // Alert-volume checkpoints that force a floor on incident severity —
    // never de-escalates, only raises severity to at least the mapped level.
    private static readonly (int MinAlertCount, IncidentSeverity Floor)[] VolumeEscalationSteps =
    [
        (20, IncidentSeverity.Critical),
        (5, IncidentSeverity.High),
    ];

    private static IncidentSeverity? VolumeEscalationFloor(int alertCount) =>
        VolumeEscalationSteps.Where(s => alertCount >= s.MinAlertCount).Select(s => (IncidentSeverity?)s.Floor).FirstOrDefault();

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly IQueueSender _queueSender;
    private readonly string _incidentCreationQueueUrl;
    private readonly IFingerprintStore _fingerprintStore;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment(),
        new SqsQueueSender(
            Environment.GetEnvironmentVariable("AWS_REGION")
                ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.")),
        Environment.GetEnvironmentVariable("INCIDENT_CREATION_QUEUE_URL")
            ?? throw new InvalidOperationException("INCIDENT_CREATION_QUEUE_URL environment variable is not set."),
        DynamoDbFingerprintStore.FromEnvironment())
    {
    }

    public Function(
        string connectionString, IEventPublisher eventPublisher, IQueueSender queueSender, string incidentCreationQueueUrl,
        IFingerprintStore fingerprintStore)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _queueSender = queueSender;
        _incidentCreationQueueUrl = incidentCreationQueueUrl;
        _fingerprintStore = fingerprintStore;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var detail = envelope.DeserializeDetail<AlertValidatedDetail>();
        EventSchemaValidator.Validate(detail);

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None);
        if (claimState == ClaimState.AlreadyCompleted)
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        if (claimState == ClaimState.PendingCompletion)
        {
            // A prior attempt already committed its business-state write (the
            // claim row itself, for the "unique" branch, or the incident
            // attach for the "duplicate" branch — see below) but crashed/
            // failed before the hand-off/publish made it out. Don't re-touch
            // the fingerprint store or re-derive which branch this was — just
            // replay exactly what was captured then.
            WorkerLog.Info(context, WorkerName, "Retrying outbound publish for a previously-claimed event.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            var pending = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
            await OutboxPublisher.PublishAllAsync(_eventPublisher, _queueSender, null, pending, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            return;
        }

        var alert = await db.Alerts.Include(a => a.Integration).FirstAsync(a => a.Id == detail.AlertId);
        var errorCode = AlertFingerprint.ExtractErrorCode(alert.Metadata);
        var fingerprint = AlertFingerprint.Compute(
            detail.OrganizationId, detail.Title, errorCode, alert.Integration?.ServiceId, detail.Environment);

        var touched = await _fingerprintStore.TouchAsync(fingerprint, FingerprintTtl, CancellationToken.None);

        if (touched.AlertCount == 1)
        {
            // First alert for this fingerprint — commit the idempotency claim
            // (with the hand-off message captured as a pending outbox item,
            // not yet sent) and hand off. Only the caller that observes
            // AlertCount == 1 from the atomic Touch ever takes this branch,
            // even under concurrent invocations, so this can't race with
            // another "unique" decision for the same fingerprint.
            var request = new IncidentCreationRequest(
                Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow, detail.AlertId, fingerprint);
            var items = new List<OutboxItem>
            {
                OutboxItem.Queue(_incidentCreationQueueUrl, JsonSerializer.Serialize(request, EventJson.Options), detail.CorrelationId),
            };
            claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);
            await db.SaveChangesAsync(CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Alert is unique, handing off to incident-creation.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { fingerprint });

            await OutboxPublisher.PublishAllAsync(_eventPublisher, _queueSender, null, items, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            return;
        }

        if (touched.IsPending)
        {
            // Another concurrent alert claimed this fingerprint first and its
            // incident-creation worker hasn't finished yet. Don't commit the
            // idempotency claim, so SQS redelivers this message after the
            // visibility timeout and it attaches once the incident exists.
            WorkerLog.Info(context, WorkerName, "Fingerprint still pending incident creation, will retry.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { fingerprint });
            throw new FingerprintPendingException(fingerprint);
        }

        var incidentId = Guid.Parse(touched.IncidentId);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId);
        if (incident is null)
        {
            // The incident this fingerprint pointed to is gone (manual
            // deletion, etc). Release the stale fingerprint and retry as if
            // this alert were new.
            await _fingerprintStore.ReleaseAsync(fingerprint, CancellationToken.None);
            WorkerLog.Warn(context, WorkerName, "Fingerprint pointed at a missing incident, released for retry.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { fingerprint, incidentId });
            throw new FingerprintPendingException(fingerprint);
        }

        alert.IncidentId = incident.Id;

        var oldCount = incident.AlertCount;

        // Atomic SQL-side increment (SET AlertCount = AlertCount + 1) instead
        // of a read-modify-write on the in-memory value — concurrent
        // duplicate-alert processing for the same incident would otherwise
        // lose updates (two invocations both reading AlertCount == N both
        // write back N + 1 instead of N + 2).
        await db.Incidents.Where(i => i.Id == incident.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AlertCount, i => i.AlertCount + 1), CancellationToken.None);
        var newCount = await db.Incidents.Where(i => i.Id == incident.Id)
            .Select(i => i.AlertCount).FirstAsync(CancellationToken.None);

        // Sync the tracked entity's in-memory value for the severity-floor
        // check below, but tell the change tracker not to write AlertCount
        // again in the SaveChangesAsync below — that column was already
        // updated atomically above, and re-including it here (still holding
        // the pre-increment snapshot as EF's baseline) would stomp any
        // further concurrent increment that landed in between.
        incident.AlertCount = newCount;
        db.Entry(incident).Property(i => i.AlertCount).IsModified = false;

        var oldSeverity = incident.Severity;
        var volumeFloor = VolumeEscalationFloor(incident.AlertCount);
        if (volumeFloor is not null && volumeFloor < incident.Severity)
        {
            incident.Severity = volumeFloor.Value;
        }

        var wasResolved = incident.Status == IncidentStatus.Resolved;
        if (wasResolved)
        {
            incident.Status = IncidentStatus.Reopened;
            incident.ResolvedAtUtc = null;
        }

        var attachItems = new List<OutboxItem>
        {
            OutboxItem.EventBridge(EventSources.DeduplicationWorker, EventTypes.IncidentUpdated,
                new IncidentUpdatedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, "AlertCount", oldCount.ToString(), incident.AlertCount.ToString())),
        };

        if (oldSeverity != incident.Severity)
        {
            attachItems.Add(OutboxItem.EventBridge(EventSources.DeduplicationWorker, EventTypes.IncidentUpdated,
                new IncidentUpdatedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, "Severity", oldSeverity.ToString(), incident.Severity.ToString())));
        }

        if (wasResolved)
        {
            attachItems.Add(OutboxItem.EventBridge(EventSources.DeduplicationWorker, EventTypes.IncidentUpdated,
                new IncidentUpdatedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, "Status", IncidentStatus.Resolved.ToString(), IncidentStatus.Reopened.ToString())));
        }

        claimRecord.PendingOutboxJson = OutboxItem.SerializeList(attachItems);
        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Duplicate alert attached to existing incident.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id, fingerprint });
        WorkerMetrics.Emit("DuplicateAlerts", 1, dimensions: new Dictionary<string, string> { ["Worker"] = WorkerName });

        await OutboxPublisher.PublishAllAsync(_eventPublisher, _queueSender, null, attachItems, CancellationToken.None);
        await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
    }
}
