using System.Collections.Concurrent;
using SentinelOps.Api.Ingestion;

namespace SentinelOps.Api.Tests;

// Replaces SqsAlertQueuePublisher in the test host so tests don't need a real
// SQS queue — records what would have been published for assertions instead.
public class FakeAlertQueuePublisher : IAlertQueuePublisher
{
    private readonly ConcurrentBag<AlertQueueMessage> _published = [];

    public IReadOnlyCollection<AlertQueueMessage> Published => _published;

    public Task PublishAsync(AlertQueueMessage message, CancellationToken ct)
    {
        _published.Add(message);
        return Task.CompletedTask;
    }
}
