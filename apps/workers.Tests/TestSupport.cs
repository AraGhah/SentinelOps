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

// One Postgres container shared across every test in the "Workers" collection —
// same rationale as apps/api.Tests/ApiTestFixture: worker logic leans on real
// EF/Npgsql behavior (query filters, unique indexes), so InMemory isn't a
// faithful enough substitute.
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

    // Workers query through org-scoped query filters, so tests read back through
    // the same kind of org-scoped context a worker would use, rather than an
    // unscoped one that would silently see everything.
    public SentinelOpsDbContext CreateOrgScopedDb(Guid organizationId) =>
        WorkerDbContextFactory.Create(ConnectionString, organizationId);
}

[CollectionDefinition("Workers")]
public class WorkersCollection : ICollectionFixture<WorkerTestFixture>;

// Records what would have been published for assertions, same pattern as
// apps/api.Tests/FakeEventPublisher.cs.
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
    public record SentMessage(string QueueUrl, string Body);

    private readonly ConcurrentBag<SentMessage> _sent = [];
    public IReadOnlyCollection<SentMessage> Sent => _sent;

    public Task SendAsync(string queueUrl, string body, CancellationToken ct)
    {
        _sent.Add(new SentMessage(queueUrl, body));
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

// In-memory stand-in for DynamoDbFingerprintStore. A single lock around every
// operation deliberately mirrors DynamoDB's per-item atomicity guarantee (one
// UpdateItem call is atomic; it doesn't need to be lock-free to be a faithful
// test double), which is what lets DeduplicationWorkerTests exercise real
// concurrent-invocation races without a live DynamoDB.
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

// Builds the same SQS message body shape a real EventBridge-rule-targeting-SQS
// delivery would produce, so tests exercise EventBridgeEnvelope.Parse exactly
// like production does.
public static class SqsEventFactory
{
    public static SQSEvent.SQSMessage Wrap(string source, string detailType, IEventDetail detail)
    {
        // "detail-type" isn't a legal C# identifier, so this is built as a
        // dictionary rather than an anonymous object with [JsonPropertyName].
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
