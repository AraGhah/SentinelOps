namespace SentinelOps.Api.Domain;

// One row per (worker, event) pair a worker has successfully processed. SQS is
// at-least-once, so every worker checks this before doing anything else and
// inserts into it as part of the same transaction as its side effects — a
// redelivered message becomes a no-op instead of double-processing.
public class ProcessedWorkerEvent
{
    public Guid Id { get; set; }
    public required string WorkerName { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

public enum NotificationStatus { Requested, Delivered, Failed }

// Created by the responder-assignment worker alongside `notification.requested`;
// updated in place by the notification worker once delivery is attempted.
public class Notification : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public Guid RecipientUserId { get; set; }
    public required string Channel { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Requested;
    public string? FailureReason { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public DateTimeOffset? FailedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}

// Write-only sink for the analytics worker — one row per event observed on the
// bus. Not tenant-scoped/query-filtered: nothing outside the analytics worker
// reads this table yet, and cross-org aggregation is exactly what it's for.
public class AnalyticsEvent
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid SourceEventId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
