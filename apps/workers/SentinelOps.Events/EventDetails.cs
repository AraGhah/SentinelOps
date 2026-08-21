namespace SentinelOps.Events;

// SchemaVersion is checked by EventSchemaValidator before a worker acts on a message.
public interface IEventDetail
{
    Guid EventId { get; }
    Guid OrganizationId { get; }
    Guid CorrelationId { get; }
    DateTimeOffset OccurredAtUtc { get; }
    string SchemaVersion { get; }
}

public record AlertReceivedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid AlertId, Guid IntegrationId, string ExternalId, string Source, string Title,
    Severity Severity, DateTimeOffset TimestampUtc, string Environment, string? Region,
    string SchemaVersion = "1.0") : IEventDetail;

public record AlertValidatedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid AlertId, string ExternalId, string Source, string Title, Severity Severity,
    string Environment, string? Region,
    string SchemaVersion = "1.0") : IEventDetail;

public record AlertRejectedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid AlertId, string Reason,
    string SchemaVersion = "1.0") : IEventDetail;

public record IncidentCreatedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid IncidentId, Guid AlertId, Guid? ServiceId, string Title, Severity Severity,
    string SchemaVersion = "1.0") : IEventDetail;

public record IncidentUpdatedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid IncidentId, string Field, string? OldValue, string? NewValue,
    string SchemaVersion = "1.0") : IEventDetail;

public record IncidentAcknowledgedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid IncidentId, Guid AcknowledgedByUserId,
    string SchemaVersion = "1.0") : IEventDetail;

public record IncidentEscalatedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid IncidentId, int FromLevel, int ToLevel, Guid? AssignedUserId,
    string SchemaVersion = "1.0") : IEventDetail;

public record IncidentResolvedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid IncidentId, Guid ResolvedByUserId,
    string SchemaVersion = "1.0") : IEventDetail;

public record NotificationRequestedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid NotificationId, Guid IncidentId, Guid RecipientUserId, string Channel,
    string SchemaVersion = "1.0") : IEventDetail;

public record NotificationDeliveredDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid NotificationId,
    string SchemaVersion = "1.0") : IEventDetail;

public record NotificationFailedDetail(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc,
    Guid NotificationId, string FailureReason,
    string SchemaVersion = "1.0") : IEventDetail;

// Not an EventBridge event/detail-type. Deduplication worker sends this directly to
// the incident-creation worker's SQS queue to avoid a create-incident race between
// two independent listeners of `alert.validated`.
public record IncidentCreationRequest(
    Guid EventId, Guid OrganizationId, Guid CorrelationId, DateTimeOffset OccurredAtUtc, Guid AlertId, string Fingerprint);
