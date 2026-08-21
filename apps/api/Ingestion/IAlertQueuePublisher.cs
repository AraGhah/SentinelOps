using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

public record AlertQueueMessage(
    Guid AlertId, Guid OrganizationId, Guid IntegrationId, Guid CorrelationId, string ExternalId, string Source,
    string Title, IncidentSeverity Severity, DateTimeOffset TimestampUtc, string Environment, string? Region);

// Named for what it publishes (an alert), not the transport underneath (currently
// an `alert.received` EventBridge event), so callers don't need to know about envelopes.
public interface IAlertQueuePublisher
{
    Task PublishAsync(AlertQueueMessage message, CancellationToken ct);
}
