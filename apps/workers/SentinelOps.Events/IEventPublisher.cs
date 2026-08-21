namespace SentinelOps.Events;

public interface IEventPublisher
{
    // detailType is one of EventTypes.*; source is one of EventSources.*.
    Task PublishAsync(string source, string detailType, IEventDetail detail, CancellationToken ct);
}
