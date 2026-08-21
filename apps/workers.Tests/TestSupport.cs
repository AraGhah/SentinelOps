using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Shared;
using Testcontainers.PostgreSql;

namespace SentinelOps.Workers.Tests;

// Shared Postgres container for the "Workers" collection; worker logic relies on real
// EF/Npgsql behavior (query filters, unique indexes) that InMemory can't fake.
public class WorkerTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("sentinelops_workers_test")
        .WithUsername("sentinelops")
        .WithPassword("sentinelops")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = WorkerDbContextFactory.CreateUnscoped(ConnectionString);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public SentinelOpsDbContext CreateOrgScopedDb(Guid organizationId) =>
        WorkerDbContextFactory.Create(ConnectionString, organizationId);
}

[CollectionDefinition("Workers")]
public class WorkersCollection : ICollectionFixture<WorkerTestFixture>;

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

public class FakeQueueSender : IQueueSender
{
    public record SentMessage(string QueueUrl, string Body, Guid CorrelationId);

    private readonly ConcurrentBag<SentMessage> _sent = [];
    public IReadOnlyCollection<SentMessage> Sent => _sent;

    public Task SendAsync(string queueUrl, string body, Guid correlationId, CancellationToken ct)
    {
        _sent.Add(new SentMessage(queueUrl, body, correlationId));
        return Task.CompletedTask;
    }
}

// Fails the first `failCount` calls (simulated PutEvents failure), then behaves normally.
// Used to test that redelivery retries the publish without redoing business logic.
public class FlakyEventPublisher(int failCount = 1) : IEventPublisher
{
    private int _calls;

    public ConcurrentBag<FakeEventPublisher.PublishedEvent> Published { get; } = [];

    public Task PublishAsync(string source, string detailType, IEventDetail detail, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        if (call <= failCount)
        {
            throw new InvalidOperationException($"Simulated publish failure (attempt {call}).");
        }

        Published.Add(new FakeEventPublisher.PublishedEvent(source, detailType, detail));
        return Task.CompletedTask;
    }
}

// Same idea as FlakyEventPublisher, for the dedup worker's SQS hand-off to incident-creation.
public class FlakyQueueSender(int failCount = 1) : IQueueSender
{
    private int _calls;

    public ConcurrentBag<FakeQueueSender.SentMessage> Sent { get; } = [];

    public Task SendAsync(string queueUrl, string body, Guid correlationId, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        if (call <= failCount)
        {
            throw new InvalidOperationException($"Simulated send failure (attempt {call}).");
        }

        Sent.Add(new FakeQueueSender.SentMessage(queueUrl, body, correlationId));
        return Task.CompletedTask;
    }
}

public class FakeEscalationStarter : IEscalationStarter
{
    public record StartedExecution(string StateMachineArn, string ExecutionName, string InputJson);

    private readonly ConcurrentBag<StartedExecution> _started = [];
    public IReadOnlyCollection<StartedExecution> Started => _started;

    public Task StartExecutionAsync(string stateMachineArn, string executionName, string inputJson, CancellationToken ct)
    {
        _started.Add(new StartedExecution(stateMachineArn, executionName, inputJson));
        return Task.CompletedTask;
    }
}

// In-memory stand-in for DynamoDbFingerprintStore. Locks around every op to mirror
// DynamoDB's per-item atomicity, so DeduplicationWorkerTests can exercise concurrent-invocation races.
public class FakeFingerprintStore : IFingerprintStore
{
    private class Entry
    {
        public string IncidentId = FingerprintRecord.Pending;
        public long AlertCount;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _items = [];

    public Task<FingerprintRecord> TouchAsync(string fingerprint, TimeSpan ttl, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(fingerprint, out var entry))
            {
                entry = new Entry();
                _items[fingerprint] = entry;
            }

            entry.AlertCount++;
            return Task.FromResult(new FingerprintRecord(fingerprint, entry.IncidentId, entry.AlertCount));
        }
    }

    public Task SetIncidentIdAsync(string fingerprint, Guid incidentId, TimeSpan ttl, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(fingerprint, out var entry) && entry.IncidentId == FingerprintRecord.Pending)
            {
                entry.IncidentId = incidentId.ToString();
            }
            return Task.CompletedTask;
        }
    }

    public Task ReleaseAsync(string fingerprint, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(fingerprint, out var entry) && entry.IncidentId == FingerprintRecord.Pending)
            {
                _items.Remove(fingerprint);
            }
            return Task.CompletedTask;
        }
    }
}

// Builds the SQS message body shape a real EventBridge-to-SQS delivery produces,
// so tests exercise EventBridgeEnvelope.Parse like production does.
public static class SqsEventFactory
{
    public static SQSEvent.SQSMessage Wrap(string source, string detailType, IEventDetail detail)
    {
        // "detail-type" isn't a legal C# identifier, hence the dictionary instead of an anonymous object.
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["source"] = source,
            ["time"] = DateTimeOffset.UtcNow,
            ["detail-type"] = detailType,
            ["detail"] = JsonSerializer.SerializeToElement(detail, detail.GetType(), EventJson.Options),
        });

        return new SQSEvent.SQSMessage { Body = json };
    }

    public static ILambdaContext Context() => new TestLambdaContext();
}

public class FakeTokenValidator(string? validTokenSub = null) : ITokenValidator
{
    public Task<string?> ValidateAsync(string? accessToken, CancellationToken ct) =>
        Task.FromResult(accessToken is not null && validTokenSub is not null && accessToken == "valid-token" ? validTokenSub : null);
}

public class FakeConnectionStore : IConnectionStore
{
    private readonly ConcurrentDictionary<string, Guid> _connections = new();
    public IReadOnlyDictionary<string, Guid> Connections => _connections;

    public Task AddAsync(string connectionId, Guid organizationId, TimeSpan ttl, CancellationToken ct)
    {
        _connections[connectionId] = organizationId;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string connectionId, CancellationToken ct)
    {
        _connections.TryRemove(connectionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid organizationId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(
            _connections.Where(c => c.Value == organizationId).Select(c => c.Key).ToList());
}

public class FakeConnectionBroadcaster : IConnectionBroadcaster
{
    public record PostedMessage(string ConnectionId, byte[] Payload);

    private readonly HashSet<string> _goneConnectionIds;
    private readonly ConcurrentBag<PostedMessage> _posted = [];
    public IReadOnlyCollection<PostedMessage> Posted => _posted;

    public FakeConnectionBroadcaster(params string[] goneConnectionIds) => _goneConnectionIds = [.. goneConnectionIds];

    public Task<bool> TryPostAsync(string connectionId, byte[] payload, CancellationToken ct)
    {
        if (_goneConnectionIds.Contains(connectionId)) return Task.FromResult(false);

        _posted.Add(new PostedMessage(connectionId, payload));
        return Task.FromResult(true);
    }
}
