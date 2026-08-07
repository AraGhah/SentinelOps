using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SentinelOps.Workers.Notification;

// Consumes `notification.requested`. Attempts delivery via INotificationChannel
// (currently a logging stub — see that file), updates the Notification row, and
// publishes `notification.delivered` or `notification.failed`.
public class Function
{
    public const string WorkerName = "notification";

    private readonly string _connectionString;
    private readonly IEventPublisher _eventPublisher;
    private readonly INotificationChannel _channel;

    public Function() : this(
        Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? throw new InvalidOperationException("CONNECTION_STRING environment variable is not set."),
        EventBridgeEventPublisher.FromEnvironment(),
        new LoggingNotificationChannel())
    {
    }

    public Function(string connectionString, IEventPublisher eventPublisher, INotificationChannel channel)
    {
        _connectionString = connectionString;
        _eventPublisher = eventPublisher;
        _channel = channel;
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
        var detail = envelope.DeserializeDetail<NotificationRequestedDetail>();
        EventSchemaValidator.Validate(detail);

        await using var db = WorkerDbContextFactory.Create(_connectionString, detail.OrganizationId);

        if (!await IdempotencyGuard.TryClaimAsync(db, WorkerName, detail.EventId, CancellationToken.None))
        {
            WorkerLog.Info(context, WorkerName, "Duplicate delivery, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId);
            return;
        }

        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == detail.NotificationId);
        if (notification is null)
        {
            WorkerLog.Warn(context, WorkerName, "Notification record no longer exists, skipping.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = detail.NotificationId });
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var result = await _channel.SendAsync(
            detail.RecipientUserId, detail.Channel, $"Incident {detail.IncidentId} requires your attention.", CancellationToken.None);

        if (result.Success)
        {
            notification.Status = NotificationStatus.Delivered;
            notification.DeliveredAtUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            notification.Status = NotificationStatus.Failed;
            notification.FailedAtUtc = DateTimeOffset.UtcNow;
            notification.FailureReason = result.FailureReason;
        }

        await db.SaveChangesAsync(CancellationToken.None);

        if (result.Success)
        {
            WorkerLog.Info(context, WorkerName, "Notification delivered.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id });

            await _eventPublisher.PublishAsync(EventSources.NotificationWorker, EventTypes.NotificationDelivered,
                new NotificationDeliveredDetail(Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow, notification.Id),
                CancellationToken.None);
        }
        else
        {
            WorkerLog.Warn(context, WorkerName, "Notification delivery failed.",
                detail.EventId, detail.OrganizationId, detail.CorrelationId, new { notificationId = notification.Id, result.FailureReason });

            await _eventPublisher.PublishAsync(EventSources.NotificationWorker, EventTypes.NotificationFailed,
                new NotificationFailedDetail(
                    Guid.NewGuid(), detail.OrganizationId, detail.CorrelationId, DateTimeOffset.UtcNow,
                    notification.Id, result.FailureReason ?? "Unknown failure."),
                CancellationToken.None);
        }
    }
}
