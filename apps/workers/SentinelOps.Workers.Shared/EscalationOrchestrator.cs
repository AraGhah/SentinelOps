using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Events;

namespace SentinelOps.Workers.Shared;

// Shared by SentinelOps.Workers.ResponderAssignment (on incident.created) and
// SentinelOps.Workers.Escalation's Restart handler (on a reopened incident) —
// both need the identical "assign whoever's on call/first-level, notify them,
// and if an escalation policy applies, kick off the escalation state machine"
// sequence.
//
// Callers own the IdempotencyGuard claim (see IdempotencyGuard/OutboxItem):
// this only ever runs for a fresh ClaimState.Claimed — a caller that observes
// ClaimState.PendingCompletion handles the "replay the captured outbox,
// don't redo business writes" branch itself before ever calling in here, the
// same way Deduplication/IncidentCreation/Notification do.
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
            // No outbound publish for this outcome — the claim is fully done
            // once the caller's subsequent SaveChangesAsync commits.
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

        // Commit the business writes (assignment, notification row, escalation
        // level, timeline entry) together with the claim and its captured
        // outbox — Completed stays false until the publish below succeeds, so
        // a crash in between doesn't silently drop the assignment notification
        // or the escalation state machine kick-off.
        claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);
        await db.SaveChangesAsync(ct);

        await OutboxPublisher.PublishAllAsync(eventPublisher, null, escalationStarter, items, ct);
        await IdempotencyGuard.CompleteAsync(db, workerName, eventId, ct);

        return responderId;
    }
}
