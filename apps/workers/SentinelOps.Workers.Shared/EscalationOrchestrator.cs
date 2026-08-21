using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Events;

namespace SentinelOps.Workers.Shared;

// Shared by ResponderAssignment (on incident.created) and Escalation's
// Restart handler (on a reopened incident): assign the on-call responder,
// notify them, and kick off the escalation state machine if a policy applies.
//
// Callers own the IdempotencyGuard claim and only call in for a fresh
// ClaimState.Claimed; PendingCompletion is handled by the caller replaying
// the outbox instead.
public static class EscalationOrchestrator
{
    public static async Task<Guid?> AssignAndMaybeEscalateAsync(
        SentinelOpsDbContext db, IEventPublisher eventPublisher, IEscalationStarter escalationStarter, string stateMachineArn,
        string workerName, Guid eventId, ProcessedWorkerEvent claimRecord,
        Guid organizationId, Guid correlationId, Incident incident, CancellationToken ct)
    {
        var responderId = await ResponderResolver.ResolveAsync(db, incident.ServiceId, DateTimeOffset.UtcNow);
        if (responderId is null)
        {
            // No outbound publish needed — claim completes on the caller's SaveChangesAsync.
            claimRecord.Completed = true;
            return null;
        }

        incident.AssignedResponderUserId = responderId;
        incident.Status = IncidentStatus.Assigned;

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            IncidentId = incident.Id,
            RecipientUserId = responderId.Value,
            Channel = "email",
            Kind = NotificationKind.IncidentAssigned,
            Status = NotificationStatus.Requested,
            RequestedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(notification);

        var policy = await ResponderResolver.ResolveEscalationPolicyAsync(db, incident.ServiceId);
        var firstLevel = policy?.Levels.OrderBy(l => l.Order).FirstOrDefault();
        if (firstLevel is not null)
        {
            incident.CurrentEscalationLevel = firstLevel.Order;
        }

        // Automated action, no human actor — the timeline shows this as "System".
        IncidentTimeline.Record(db, organizationId, incident.Id, IncidentEventType.Assigned, actorUserId: null,
            details: new { AssignedResponderUserId = responderId });

        var items = new List<OutboxItem>
        {
            OutboxItem.EventBridge(EventSources.ResponderAssignmentWorker, EventTypes.IncidentUpdated,
                new IncidentUpdatedDetail(
                    Guid.NewGuid(), organizationId, correlationId, DateTimeOffset.UtcNow,
                    incident.Id, "AssignedResponderUserId", null, responderId.Value.ToString())),
            OutboxItem.EventBridge(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested,
                new NotificationRequestedDetail(
                    Guid.NewGuid(), organizationId, correlationId, DateTimeOffset.UtcNow,
                    notification.Id, incident.Id, responderId.Value, notification.Channel)),
        };

        if (firstLevel is not null)
        {
            var input = new EscalationStateInput(
                "CheckStatus", organizationId, incident.Id, correlationId, policy!.Id,
                firstLevel.Order, firstLevel.AckTimeoutMinutes * 60, FallbackNotified: false);

            items.Add(OutboxItem.StepFunctions(
                stateMachineArn, $"incident-{incident.Id:N}-{Guid.NewGuid():N}",
                System.Text.Json.JsonSerializer.Serialize(input, EventJson.Options), correlationId));
        }

        // Completed stays false until the publish below succeeds, so a crash
        // in between doesn't silently drop the notification or state machine kick-off.
        claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);
        await db.SaveChangesAsync(ct);

        await OutboxPublisher.PublishAllAsync(eventPublisher, null, escalationStarter, items, ct);
        await IdempotencyGuard.CompleteAsync(db, workerName, eventId, ct);

        return responderId;
    }
}
