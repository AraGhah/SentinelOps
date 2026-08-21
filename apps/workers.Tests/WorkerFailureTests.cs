using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.AlertValidation;

namespace SentinelOps.Workers.Tests;

// Failure-mode tests for the SQS-triggered workers: a malformed message, an
// unknown event-schema version, and the database being unreachable. All three
// exercise SqsBatchProcessor's guarantee (see SentinelOps.Workers.Shared) that
// one bad record in a batch is reported as a single batch item failure — so
// SQS redelivers just that message — rather than throwing out of
// FunctionHandler and taking the whole batch (including unrelated, valid
// messages) down with it.
[Collection("Workers")]
public class WorkerFailureTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_MalformedMessageBody_IsIsolatedFromOtherMessagesInTheBatch()
    {
        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var malformed = new SQSEvent.SQSMessage { MessageId = Guid.NewGuid().ToString(), Body = "{ not valid json" };
        var valid = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, BuildDetail());

        var response = await function.FunctionHandler(new SQSEvent { Records = [malformed, valid] }, SqsEventFactory.Context());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal(malformed.MessageId, failure.ItemIdentifier);

        // The well-formed message elsewhere in the same batch still processed
        // normally — the parse failure on record 1 didn't abort record 2.
        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.AlertValidated, published.DetailType);
    }

    [Fact]
    public async Task Handle_UnsupportedSchemaVersion_IsIsolatedFromOtherMessagesInTheBatch()
    {
        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var unknownVersion = SqsEventFactory.Wrap(
            EventSources.Api, EventTypes.AlertReceived, BuildDetail() with { SchemaVersion = "99.0" });
        var valid = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, BuildDetail());

        var response = await function.FunctionHandler(new SQSEvent { Records = [unknownVersion, valid] }, SqsEventFactory.Context());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal(unknownVersion.MessageId, failure.ItemIdentifier);

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.AlertValidated, published.DetailType);
    }

    [Fact]
    public async Task Handle_DatabaseUnreachable_ReportsBatchItemFailureInsteadOfCrashing()
    {
        // A connection string pointing at a closed local port fails fast
        // (connection refused) rather than hanging for the default Npgsql
        // timeout, so this test doesn't need to actually stop the shared
        // Postgres container other tests in this collection depend on.
        const string unreachableConnectionString =
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2";

        var publisher = new FakeEventPublisher();
        var function = new Function(unreachableConnectionString, publisher);

        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, BuildDetail());

        var response = await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        // Redelivered by SQS once the visibility timeout elapses — by then the
        // database is expected to be back, per the automatic retry/DLQ policy
        // configured on every worker queue (see EventProcessingStack).
        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal(message.MessageId, failure.ItemIdentifier);
        Assert.Empty(publisher.Published);
    }

    private static AlertReceivedDetail BuildDetail() => new(
        EventId: Guid.NewGuid(), OrganizationId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(), OccurredAtUtc: DateTimeOffset.UtcNow,
        AlertId: Guid.NewGuid(), IntegrationId: Guid.NewGuid(), ExternalId: $"ext-{Guid.NewGuid()}", Source: "datadog",
        Title: "High latency detected", Severity: Severity.High, TimestampUtc: DateTimeOffset.UtcNow,
        Environment: "production", Region: "us-east-1");
}
