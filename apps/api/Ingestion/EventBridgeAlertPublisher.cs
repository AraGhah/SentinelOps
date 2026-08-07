using SentinelOps.Events;

namespace SentinelOps.Api.Ingestion;

public class EventBridgeAlertPublisher(IEventPublisher eventPublisher) : IAlertQueuePublisher
{
    public Task PublishAsync(AlertQueueMessage message, CancellationToken ct)
    {
        var detail = new AlertReceivedDetail(
            EventId: Guid.NewGuid(),
            OrganizationId: message.OrganizationId,
            CorrelationId: message.CorrelationId,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            AlertId: message.AlertId,
            IntegrationId: message.IntegrationId,
            ExternalId: message.ExternalId,
            Source: message.Source,
            Title: message.Title,
            Severity: (Severity)message.Severity,
            TimestampUtc: message.TimestampUtc,
            Environment: message.Environment,
            Region: message.Region);

        EventSchemaValidator.Validate(detail);

        return eventPublisher.PublishAsync(EventSources.Api, EventTypes.AlertReceived, detail, ct);
    }
}
