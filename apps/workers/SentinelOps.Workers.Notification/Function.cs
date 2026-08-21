using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Notifications;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Notification;

// Consumes `notification.requested`. Renders an email from the Notification
// row's Kind (see EmailTemplates), honors the recipient's NotificationPreference
// (channel disabled / quiet hours -> Suppressed, no send attempted), and
// attempts delivery via INotificationChannel — SES in production, a logging
// stub in tests. Updates the Notification row and publishes
// `notification.delivered` or `notification.failed`.
public class Function
{
    public const string WorkerName = "notification";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly INotificationChannel _channel;

    public Function() : this(
        DbConnectionStringResolver.FromEnvironment(),
        EventBridgeEventPublisher.FromEnvironment(),
        SesNotificationChannel.FromEnvironment())
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher, INotificationChannel channel)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _channel = channel;
    }

    public Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context) =>
        SqsBatchProcessor.RunAsync(sqsEvent, context, WorkerName, record => HandleAsync(record, context));

    private async Task HandleAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var envelope = EventBridgeEnvelope.Parse(record.Body);
        var detail = envelope.DeserializeDetail<NotificationRequestedDetail>();
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
            // A prior attempt already updated the Notification row (Delivered/
            // Failed/Suppressed) and committed that — do NOT re-attempt
            // delivery through _channel (that could double-send the email).
            // Just replay the captured publish, if any, and finish.
            WorkerLog.Info(context, WorkerName, "Retrying outbound publish for a previously-claimed event.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            var pending = OutboxItem.DeserializeList(claimRecord.PendingOutboxJson);
            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, pending, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            return;
        }

        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == detail.NotificationId);
        if (notification is null)
        {
            WorkerLog.Warn(context, WorkerName, "Notification record no longer exists, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = detail.NotificationId });
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var preference = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == detail.RecipientUserId);
        if (!NotificationPreferenceEvaluator.IsChannelEnabled(preference, detail.Channel)
            || NotificationPreferenceEvaluator.IsQuietHoursActive(preference, DateTimeOffset.UtcNow))
        {
            notification.Status = NotificationStatus.Suppressed;
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Notification suppressed by recipient preference.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id });
            return;
        }

        var recipient = await db.Users.FirstOrDefaultAsync(u => u.Id == detail.RecipientUserId);
        if (recipient is null)
        {
            notification.Status = NotificationStatus.Failed;
            notification.FailedAtUtc = DateTimeOffset.UtcNow;
            notification.FailureReason = "Recipient no longer exists.";

            var missingRecipientItems = new List<OutboxItem>
            {
                OutboxItem.EventBridge(EventSources.NotificationWorker, EventTypes.NotificationFailed,
                    new NotificationFailedDetail(
                        Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                        notification.Id, notification.FailureReason)),
            };
            claimRecord.PendingOutboxJson = OutboxItem.SerializeList(missingRecipientItems);
            await db.SaveChangesAsync(CancellationToken.None);

            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, missingRecipientItems, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            WorkerMetrics.Emit("NotificationFailures", 1, dimensions: new Dictionary<string, string> { ["Worker"] = WorkerName });
            return;
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == detail.IncidentId);
        if (incident is null)
        {
            WorkerLog.Warn(context, WorkerName, "Incident no longer exists, skipping notification.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { incidentId = detail.IncidentId });
            claimRecord.Completed = true;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var email = EmailTemplates.Render(
            notification.Kind, incident.Id, incident.Title, incident.Severity, incident.CurrentEscalationLevel);
        var result = await _channel.SendAsync(recipient.Email, email.Subject, email.Html, email.Text, CancellationToken.None);

        if (result.Success)
        {
            notification.Status = NotificationStatus.Delivered;
            notification.DeliveredAtUtc = DateTimeOffset.UtcNow;
            IncidentTimeline.Record(db, detail.OrganizationId, incident.Id, IncidentEventType.NotificationSent, actorUserId: null,
                details: new { notification.RecipientUserId, notification.Channel, notification.Kind });

            var deliveredItems = new List<OutboxItem>
            {
                OutboxItem.EventBridge(EventSources.NotificationWorker, EventTypes.NotificationDelivered,
                    new NotificationDeliveredDetail(Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow, notification.Id)),
            };
            claimRecord.PendingOutboxJson = OutboxItem.SerializeList(deliveredItems);
            await db.SaveChangesAsync(CancellationToken.None);

            WorkerLog.Info(context, WorkerName, "Notification delivered.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id });

            await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, deliveredItems, CancellationToken.None);
            await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
            return;
        }

        if (result.IsTransient)
        {
            // Leave the Notification row as Requested (this attempt never
            // happened, as far as the record is concerned) and let SQS
            // redeliver after the visibility timeout — up to the queue's
            // maxReceiveCount, after which it lands in the DLQ automatically.
            // Nothing was claimed/committed for this event, so no outbox/claim
            // cleanup is needed here — the thrown exception aborts before any
            // SaveChangesAsync for this attempt.
            WorkerLog.Warn(context, WorkerName, "Transient send failure, will retry on redelivery.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id, result.FailureReason });
            throw new TransientNotificationException(notification.Id, result.FailureReason);
        }

        notification.Status = NotificationStatus.Failed;
        notification.FailedAtUtc = DateTimeOffset.UtcNow;
        notification.FailureReason = result.FailureReason;

        var failedItems = new List<OutboxItem>
        {
            OutboxItem.EventBridge(EventSources.NotificationWorker, EventTypes.NotificationFailed,
                new NotificationFailedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    notification.Id, result.FailureReason ?? "Unknown failure.")),
        };
        claimRecord.PendingOutboxJson = OutboxItem.SerializeList(failedItems);
        await db.SaveChangesAsync(CancellationToken.None);

        WorkerLog.Warn(context, WorkerName, "Notification delivery failed permanently.",
            detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id, result.FailureReason });

        await OutboxPublisher.PublishAllAsync(_eventPublisher, null, null, failedItems, CancellationToken.None);
        await IdempotencyGuard.CompleteAsync(db, WorkerName, detail.EventId, CancellationToken.None);
        WorkerMetrics.Emit("NotificationFailures", 1, dimensions: new Dictionary<string, string> { ["Worker"] = WorkerName });
    }
}

public class TransientNotificationException(Guid notificationId, string? reason)
    : Exception($"Transient failure sending notification {notificationId}: {reason ?? "unknown reason"}");
