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
