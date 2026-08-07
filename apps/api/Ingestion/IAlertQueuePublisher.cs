using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

public record AlertQueueMessage(
    Guid AlertId, Guid OrganizationId, Guid IntegrationId, Guid CorrelationId, string ExternalId, string Source,
    string Title, IncidentSeverity Severity, DateTimeOffset TimestampUtc, string Environment, string? Region);

// Named for what it publishes (an alert), not for the transport underneath —
// today that transport is an `alert.received` EventBridge event
// (EventBridgeAlertPublisher), previously a raw SQS message. Kept as a narrow
// interface (rather than exposing IEventPublisher directly to callers) so
// AlertIngestionService doesn't need to know about EventBridge/event envelopes.
public interface IAlertQueuePublisher
{
    Task PublishAsync(AlertQueueMessage message, CancellationToken ct);
}
