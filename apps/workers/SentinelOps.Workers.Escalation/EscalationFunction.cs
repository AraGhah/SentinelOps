using System.Security.Cryptography;
using Amazon.Lambda.Core;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Escalation;

// Direct Step Functions task target (request/response, not SQS) — invoked
// once per Wait-loop iteration of the escalation state machine defined in
// infrastructure-stack.ts. See EscalationStateInput for why input and output
// share one shape.
public class EscalationFunction
{
    public const string WorkerName = "escalation";

    // Used once every defined level (plus the fallback administrator) has
    // already been notified and the incident still isn't acknowledged/resolved.
    private static readonly TimeSpan FallbackAckTimeout = TimeSpan.FromMinutes(30);

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;

    public EscalationFunction() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment())
    {
    }

    public EscalationFunction(string connectionString, IEventPublisher eventPublisher)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
    }

    public Task<EscalationStateInput> FunctionHandler(EscalationStateInput input, ILambdaContext context) => input.Action switch
    {
        "CheckStatus" => HandleCheckStatusAsync(input),
        "AdvanceLevel" => HandleAdvanceLevelAsync(input, context),
        "RecordFailure" => HandleRecordFailureAsync(input),
        _ => throw new InvalidOperationException($"Unknown escalation action '{input.Action}'."),
    };

    private async Task<EscalationStateInput> HandleCheckStatusAsync(EscalationStateInput input)
    {
        await using var db = WorkerDbContextFactory.Create(_connectionString, input.OrganizationId);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == input.IncidentId);

        // A deleted incident, or one that has moved anywhere past
        // Triggered/Assigned (Acknowledged, Investigating, Monitoring,
        // Resolved, or Reopened — the latter only reachable after Resolved,
        // and handled by a fresh execution rather than this one), means
        // there's nothing left for this execution to escalate.
        var acknowledgedOrResolved = incident is null || incident.Status is not (IncidentStatus.Triggered or IncidentStatus.Assigned);
        return input with { AcknowledgedOrResolved = acknowledgedOrResolved };
    }

    private async Task<EscalationStateInput> HandleAdvanceLevelAsync(EscalationStateInput input, ILambdaContext context)
    {
        await using var db = WorkerDbContextFactory.Create(_connectionString, input.OrganizationId);

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == input.IncidentId);
        if (incident is null)
        {
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, stopping escalation.",
                Guid.NewGuid(), input.OrganizationId, input.CorrelationId, new { incidentId = input.IncidentId });
            return input with { Stop = true };
        }

        var policy = await db.EscalationPolicies
            .Include(p => p.Levels).ThenInclude(l => l.Targets)
            .FirstOrDefaultAsync(p => p.Id == input.EscalationPolicyId);
        if (policy is null)
        {
            WorkerLog.Warn(context, WorkerName, "Escalation policy no longer exists, stopping escalation.",
                Guid.NewGuid(), input.OrganizationId, input.CorrelationId, new { policyId = input.EscalationPolicyId });
            return input with { Stop = true };
        }

        var nextLevel = policy.Levels.Where(l => l.Order > input.CurrentLevelOrder).OrderBy(l => l.Order).FirstOrDefault();
        if (nextLevel is not null)
        {
            // Step Functions can retry this task on a transient Lambda error,
            // which would otherwise re-notify the same level twice — guard
            // against that the same way every SQS-driven worker guards
            // against redelivery, keyed off (incident, level) instead of an
            // event id since there isn't one here.
            var levelEventId = DeterministicEventId(incident.Id, nextLevel.Order);
            var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, levelEventId, CancellationToken.None);

            if (claimState == ClaimState.AlreadyCompleted)
            {
                return input with { CurrentLevelOrder = nextLevel.Order, AckTimeoutSeconds = nextLevel.AckTimeoutMinutes * 60, Stop = false };
            }

            if (claimState == ClaimState.PendingCompletion)
            {
                // A prior attempt already committed the notification rows and
                // escalation-level bump but crashed/failed before publishing
                // — don't re-notify the same targets again, just replay.
                var pendingRetry = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
                await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, pendingRetry, CancellationToken.None);
                await IdempotencyGuard.CompleteAsync(db, WorkerName, levelEventId, CancellationToken.None);
                return input with { CurrentLevelOrder = nextLevel.Order, AckTimeoutSeconds = nextLevel.AckTimeoutMinutes * 60, Stop = false };
            }

            var targetUserIds = nextLevel.Targets.Select(t => t.UserId).ToList();
            var notifications = targetUserIds.Select(userId => NewNotification(input.OrganizationId, incident.Id, userId)).ToList();
            db.Notifications.AddRange(notifications);
            incident.CurrentEscalationLevel = nextLevel.Order;
            IncidentTimeline.Record(db, input.OrganizationId, incident.Id, IncidentEventType.Escalated, actorUserId: null,
                details: new { FromLevel = input.CurrentLevelOrder, ToLevel = nextLevel.Order, TargetUserIds = targetUserIds });

            var items = notifications.Select(notification => OutboxItem.EventBridge(
                EventSources.EscalationWorker, EventTypes.NotificationRequested,
                new NotificationRequestedDetail(
                    Guid.NewGuid(), input.OrganizationId, input.CorrelationId, DateTimeOffset.UtcNow,
                    notification.Id, incident.Id, notification.RecipientUserId, notification.Channel))).ToList();
            items.Add(OutboxItem.EventBridge(EventSources.EscalationWorker, EventTypes.IncidentEscalated,
                new IncidentEscalatedDetail(
                    Guid.NewGuid(), input.OrganizationId, input.CorrelationId, DateTimeOffset.UtcNow,
                    incident.Id, input.CurrentLevelOrder, nextLevel.Order, targetUserIds.FirstOrDefault())));

            claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);
            await db.SaveChangesAsync(CancellationToken.None);

            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, items, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, levelEventId, CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Escalated to next level.",
                Guid.NewGuid(), input.OrganizationId, input.CorrelationId,
                new { incidentId = incident.Id, fromLevel = input.CurrentLevelOrder, toLevel = nextLevel.Order });

            return input with { CurrentLevelOrder = nextLevel.Order, AckTimeoutSeconds = nextLevel.AckTimeoutMinutes * 60, Stop = false };
        }

        if (!input.FallbackNotified && policy.FallbackAdministratorUserId is not null)
        {
            var adminId = policy.FallbackAdministratorUserId.Value;

            // Sentinel level order (one past any real level) so the fallback
            // administrator notification is idempotency-keyed distinctly from
            // every real level, including across policies with different
            // level counts.
            const int fallbackLevelOrder = int.MaxValue;
            var fallbackEventId = DeterministicEventId(incident.Id, fallbackLevelOrder);
            var (claimState, claimRecord) = await IdempotencyGuard.TryClaimAsync(db, WorkerName, fallbackEventId, CancellationToken.None);

            if (claimState == ClaimState.AlreadyCompleted)
            {
                return input with { FallbackNotified = true, AckTimeoutSeconds = (int)FallbackAckTimeout.TotalSeconds, Stop = false };
            }

            if (claimState == ClaimState.PendingCompletion)
            {
                var pendingRetry = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
                await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, pendingRetry, CancellationToken.None);
                await IdempotencyGuard.CompleteAsync(db, WorkerName, fallbackEventId, CancellationToken.None);
                return input with { FallbackNotified = true, AckTimeoutSeconds = (int)FallbackAckTimeout.TotalSeconds, Stop = false };
            }

            var notification = NewNotification(input.OrganizationId, incident.Id, adminId);
            db.Notifications.Add(notification);
            IncidentTimeline.Record(db, input.OrganizationId, incident.Id, IncidentEventType.Escalated, actorUserId: null,
                details: new { FromLevel = input.CurrentLevelOrder, ToLevel = fallbackLevelOrder, TargetUserIds = new[] { adminId } });

            var items = new List<OutboxItem>
            {
                OutboxItem.EventBridge(EventSources.EscalationWorker, EventTypes.NotificationRequested,
                    new NotificationRequestedDetail(
                        Guid.NewGuid(), input.OrganizationId, input.CorrelationId, DateTimeOffset.UtcNow,
                        notification.Id, incident.Id, adminId, notification.Channel)),
                OutboxItem.EventBridge(EventSources.EscalationWorker, EventTypes.IncidentEscalated,
                    new IncidentEscalatedDetail(
                        Guid.NewGuid(), input.OrganizationId, input.CorrelationId, DateTimeOffset.UtcNow,
                        incident.Id, input.CurrentLevelOrder, fallbackLevelOrder, adminId)),
            };

            claimRecord.PendingOutboxJson = OutboxItem.SerializeList(items);
            await db.SaveChangesAsync(CancellationToken.None);

            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, items, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, fallbackEventId, CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Escalated to fallback administrator.",
                Guid.NewGuid(), input.OrganizationId, input.CorrelationId, new { incidentId = incident.Id, adminId });

            return input with { FallbackNotified = true, AckTimeoutSeconds = (int)FallbackAckTimeout.TotalSeconds, Stop = false };
        }

        // Every level, and the fallback administrator (if any), has already
        // been notified — escalation is exhausted. Stop rather than looping
        // back to level 1, which would notify the same people again forever.
        WorkerLog.Warn(context, WorkerName, "Escalation exhausted with no acknowledgement.",
            Guid.NewGuid(), input.OrganizationId, input.CorrelationId, new { incidentId = incident.Id });
        return input with { Stop = true };
    }

    private async Task<EscalationStateInput> HandleRecordFailureAsync(EscalationStateInput input)
    {
        await _eventPublisher.PublishAsync(EventSources.EscalationWorker, EventTypes.IncidentUpdated,
            new IncidentUpdatedDetail(
                Guid.NewGuid(), input.OrganizationId, input.CorrelationId, DateTimeOffset.UtcNow,
                input.IncidentId, "EscalationWorkflow", null, "Failed"),
            CancellationToken.None);

        return input;
    }

    private static Notification NewNotification(Guid organizationId, Guid incidentId, Guid recipientUserId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        IncidentId = incidentId,
        RecipientUserId = recipientUserId,
        Channel = "email",
        Kind = NotificationKind.IncidentEscalated,
        Status = NotificationStatus.Requested,
        RequestedAtUtc = DateTimeOffset.UtcNow,
    };

    private static Guid DeterministicEventId(Guid incidentId, int levelOrder)
    {
        var bytes = incidentId.ToByteArray().Concat(BitConverter.GetBytes(levelOrder)).ToArray();
        return new Guid(MD5.HashData(bytes));
    }
}
