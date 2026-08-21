namespace SentinelOps.Api.Domain;

// One row per (worker, event) pair a worker has claimed, inserted in the same transaction
// as its business-state writes (SQS is at-least-once). Two-phase completion (see
// IdempotencyGuard): Completed stays false until the outbound publish succeeds, so a
// crash between the two can retry just the publish (via PendingOutboxJson) instead of
// re-running business logic.
public class ProcessedWorkerEvent
{
    public Guid Id { get; set; }
    public required string WorkerName { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public bool Completed { get; set; }
    // Serialized SentinelOps.Workers.Shared.OutboxItem list still pending publish. Null
    // once Completed, or when the claim never needed an outbound publish.
    public string? PendingOutboxJson { get; set; }
}

// Suppressed = intentionally not sent (quiet hours/disabled channel); Failed = delivery attempted and rejected.
public enum NotificationStatus { Requested, Delivered, Failed, Suppressed }

// Drives which email template the notification worker renders. See EmailTemplates.
public enum NotificationKind { IncidentAssigned, IncidentEscalated, IncidentResolved }

// Created by the responder-assignment/escalation workers (and the incidents
// API, on resolution) alongside `notification.requested`; updated in place by
// the notification worker once delivery is attempted.
public class Notification : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public Guid RecipientUserId { get; set; }
    public required string Channel { get; set; }
    public NotificationKind Kind { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Requested;
    public string? FailureReason { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public DateTimeOffset? FailedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}

// Per-user, per-organization notification settings. No row means all defaults
// (email enabled, no quiet hours).
public class NotificationPreference : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public bool EmailEnabled { get; set; } = true;
    // Both null means no quiet hours. End < Start wraps midnight (see NotificationPreferenceExtensions).
    public TimeOnly? QuietHoursStartLocal { get; set; }
    public TimeOnly? QuietHoursEndLocal { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

// Write-only sink for the analytics worker, one row per event observed on the bus.
// Not tenant-scoped/query-filtered; cross-org aggregation is the point.
public class AnalyticsEvent
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid SourceEventId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
