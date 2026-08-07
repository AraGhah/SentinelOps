using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Deduplication;

// Consumes `alert.validated`. Looks for an existing, still-open incident whose
// most recent alert shares the same (ExternalId, Source, Environment)
// fingerprint within this organization. Found -> attach and publish
// `incident.updated`. Not found -> hand off directly to the incident-creation
// worker's queue (see IncidentCreationRequest for why this isn't an
// EventBridge event).
public class Function
{
    public const string WorkerName = "deduplication";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly IQueueSender _queueSender;
    private readonly string _incidentCreationQueueUrl;

    public Function() : this(
        Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException("CONNECTION_STRING environment variable is not set."),
        EventBridgeEventPublisher.FromEnvironment(),
        new SqsQueueSender(
            Environment.GetEnvironmentVariable("AWS_REGION")
                ?? throw new InvalidOperationException("AWS_REGION environment variable is not set.")),
        Environment.GetEnvironmentVariable("INCIDENT_CREATION_QUEUE_URL")
            ?? throw new InvalidOperationException("INCIDENT_CREATION_QUEUE_URL environment variable is not set."))
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher, IQueueSender queueSender, string incidentCreationQueueUrl)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _queueSender = queueSender;
        _incidentCreationQueueUrl = incidentCreationQueueUrl;
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
        var detail = envelope.DeserializeDetail<AlertValidatedDetail>();
        EventSchemaValidator.Validate(detail);

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        if (!await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None))
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        var duplicateOf = await db.Alerts
            .Include(a => a.Incident)
            .Where(a => a.Id != detail.AlertId && a.ExternalId == detail.ExternalId && a.Source == detail.Source
                && a.Environment == detail.Environment && a.IncidentId != null && a.Incident!.Status != IncidentStatus.Resolved)
            .OrderByDescending(a => a.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (duplicateOf is not null)
        {
            var thisAlert = await db.Alerts.FirstAsync(a => a.Id == detail.AlertId);
            thisAlert.IncidentId = duplicateOf.IncidentId;
            var incident = duplicateOf.Incident!;
            var oldCount = incident.AlertCount;
            incident.AlertCount++;

            await db.SaveChangesAsync(CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Duplicate alert attached to existing incident.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id });

            await _eventPublisher.PublishAsync(EventSources.DeduplicationWorker, EventTypes.IncidentUpdated,
                new IncidentUpdatedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, "AlertCount", oldCount.ToString(), incident.AlertCount.ToString()),
                CancellationToken.None);
            return;
        }

        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Alert is unique, handing off to incident-creation.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId);

        var request = new IncidentCreationRequest(Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow, detail.AlertId);
        await _queueSender.SendAsync(_incidentCreationQueueUrl, JsonSerializer.Serialize(request, EventJson.Options), CancellationToken.None);
    }
}
