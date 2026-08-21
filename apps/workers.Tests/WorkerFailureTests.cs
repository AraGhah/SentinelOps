using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.AlertValidation;

namespace SentinelOps.Workers.Tests;

// Verifies SqsBatchProcessor reports a bad record as a single batch item failure
// (so SQS redelivers just that message) instead of throwing and failing the whole batch.
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
        // Closed local port fails fast (connection refused) instead of hitting the Npgsql timeout.
        const string unreachableConnectionString =
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2";

        var publisher = new FakeEventPublisher();
        var function = new Function(unreachableConnectionString, publisher);

        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, BuildDetail());

        var response = await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

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
