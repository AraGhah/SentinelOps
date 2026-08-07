namespace SentinelOps.Events;

public interface IEventPublisher
{
    // detailType is one of EventTypes.*; source is one of EventSources.*.
    // Kept as plain strings rather than an enum-per-event overload set so a new
    // event type doesn't require a new interface method.
    Task PublishAsync(string source, string detailType, IEventDetail detail, CancellationToken ct);
}
