namespace SentinelOps.Api.Domain;

// One row per (worker, event) pair a worker has claimed. SQS is at-least-once,
// so every worker checks this before doing anything else and inserts into it
// as part of the same transaction as its business-state side effects.
//
// Two-phase completion (see IdempotencyGuard): inserting this row (Completed =
// false) reserves the work and commits atomically with the worker's business
// writes, but does NOT by itself mean the unit of work is done — the outbound
// publish/send (EventBridge, SQS, Step Functions) still has to succeed. Only
// once that publish succeeds does a second, separate update set Completed =
// true. If a worker crashes/fails between those two points, redelivery sees
// Completed == false and knows to retry ONLY the outbound publish (using
// PendingOutboxJson, captured before the business-write commit) rather than
// re-running business logic that already landed — which would otherwise risk
// duplicate incidents/records.
public class ProcessedWorkerEvent
{
    public Guid Id { get; set; }
    public required string WorkerName { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public bool Completed { get; set; }
    // Serialized SentinelOps.Workers.Shared.OutboxItem list — what still needs
    // to be published/sent for this claim to be considered Completed. Null
    // once Completed is true (CompleteAsync clears it), or when a claim never
    // needed an outbound publish in the first place.
    public string? PendingOutboxJson { get; set; }
}

// Suppressed = deliberately not sent (quiet hours active, or the recipient
// disabled the channel) — distinct from Failed, which means a real delivery
// attempt was made and rejected/errored.
public enum NotificationStatus { Requested, Delivered, Failed, Suppressed }

// What triggered the notification — drives which email template the
// notification worker renders. See SentinelOps.Workers.Notification.EmailTemplates.
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

// Per-user, per-organization notification settings. Absence of a row means
// "all defaults": email enabled, no quiet hours. Self-service only (a user
// manages their own) — see NotificationPreferencesController.
public class NotificationPreference : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public bool EmailEnabled { get; set; } = true;
    // Both null (the default) means no quiet hours configured. A window that
    // wraps midnight (End < Start) is treated the same way ScheduleRotation
    // treats an overnight shift — see NotificationPreferenceExtensions.
    public TimeOnly? QuietHoursStartLocal { get; set; }
    public TimeOnly? QuietHoursEndLocal { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
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
