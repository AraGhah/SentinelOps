using System.Collections.Concurrent;
using SentinelOps.Events;

namespace SentinelOps.Api.Tests;

// Replaces EventBridgeEventPublisher in the test host so tests don't need a real
// EventBridge bus — records what would have been published for assertions instead.
public class FakeEventPublisher : IEventPublisher
{
    public record PublishedEvent(string Source, string DetailType, IEventDetail Detail);

    private readonly ConcurrentBag<PublishedEvent> _published = [];

    public IReadOnlyCollection<PublishedEvent> Published => _published;

    public Task PublishAsync(string source, string detailType, IEventDetail detail, CancellationToken ct)
    {
        _published.Add(new PublishedEvent(source, detailType, detail));
        return Task.CompletedTask;
    }
}
