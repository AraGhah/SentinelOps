using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.ResponderAssignment;

// Consumes `incident.created`. Resolves who's on call right now for the
// incident's service (Schedule/ScheduleRotation/ScheduleOverride), falling
// back to the service's (or org's) EscalationPolicy level-1 targets if there's
// no schedule. Assigns the incident and requests a notification.
public class Function
{
    public const string WorkerName = "responder-assignment";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;

    public Function() : this(
        Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException("CONNECTION_STRING environment variable is not set."),
        EventBridgeEventPublisher.FromEnvironment())
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
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
        var detail = envelope.DeserializeDetail<IncidentCreatedDetail>();
        EventSchemaValidator.Validate(detail);

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        if (!await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None))
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == detail.IncidentId);
        if (incident is null)
        {
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, skipping assignment.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = detail.IncidentId });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var responderId = await ResolveResponderAsync(db, incident.ServiceId, DateTimeOffset.UtcNow);

        if (responderId is null)
        {
            WorkerLog.Warn(context, WorkerName, "No on-call responder or escalation target could be resolved.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        incident.AssignedResponderUserId = responderId;
        incident.Status = IncidentStatus.Assigned;

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            OrganizationId = detail.OrganizationId,
            IncidentId = incident.Id,
            RecipientUserId = responderId.Value,
            Channel = "email",
            Status = NotificationStatus.Requested,
            RequestedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(notification);

        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Info(context, WorkerName, "Responder assigned.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = incident.Id, responderId });

        await _eventPublisher.PublishAsync(EventSources.ResponderAssignmentWorker, EventTypes.IncidentUpdated,
            new IncidentUpdatedDetail(
                Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                incident.Id, "AssignedResponderUserId", null, responderId.Value.ToString()),
            CancellationToken.None);

        await _eventPublisher.PublishAsync(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested,
            new NotificationRequestedDetail(
                Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                notification.Id, incident.Id, responderId.Value, notification.Channel),
            CancellationToken.None);
    }

    private static async Task<Guid?> ResolveResponderAsync(
        Api.Data.SentinelOpsDbContext db, Guid? serviceId, DateTimeOffset nowUtc)
    {
        if (serviceId is not null)
        {
            var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.ServiceId == serviceId);
            if (schedule is not null)
            {
                var rotations = await db.ScheduleRotations.Where(r => r.ScheduleId == schedule.Id).ToListAsync();
                var overrides = await db.ScheduleOverrides.Where(o => o.ScheduleId == schedule.Id).ToListAsync();
                var onCall = OnCallResolver.Resolve(schedule, rotations, overrides, nowUtc);
                if (onCall is not null) return onCall;
            }
        }

        var policy = await db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .Where(p => p.ServiceId == serviceId)
            .FirstOrDefaultAsync()
            ?? await db.EscalationPolicies
                .Include(p => p.Levels).ThenInclude(l => l.Targets)
                .Where(p => p.ServiceId == null)
                .FirstOrDefaultAsync();

        var firstLevel = policy?.Levels.OrderBy(l => l.Order).FirstOrDefault();
        var firstTarget = firstLevel?.Targets.FirstOrDefault();
        return firstTarget?.UserId;
    }
}
