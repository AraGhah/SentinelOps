using Amazon.Lambda.SQSEvents;
using SentinelOps.Events;
using SentinelOps.Workers.AlertValidation;

namespace SentinelOps.Workers.Tests;

public class AlertValidationWorkerTests
{
    [Fact]
    public void Validate_WellFormedAlert_ReturnsNull()
    {
        var reason = Function.Validate(BuildDetail());
        Assert.Null(reason);
    }

    [Fact]
    public void Validate_TimestampTooFarInFuture_IsRejected()
    {
        var reason = Function.Validate(BuildDetail() with { TimestampUtc = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.Contains("future", reason);
    }

    [Fact]
    public void Validate_TimestampTooOld_IsRejected()
    {
        var reason = Function.Validate(BuildDetail() with { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-30) });
        Assert.Contains("old", reason);
    }

    [Fact]
    public void Validate_MissingTitle_IsRejected()
    {
        var reason = Function.Validate(BuildDetail() with { Title = "" });
        Assert.Contains("title", reason);
    }

    [Collection("Workers")]
    public class Integration(WorkerTestFixture fixture)
    {
        [Fact]
        public async Task Handle_ValidAlert_PublishesAlertValidated()
        {
            var detail = BuildDetail();
            var publisher = new FakeEventPublisher();
            var function = new Function(fixture.ConnectionString, publisher);

            var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, detail);
            await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

            var published = Assert.Single(publisher.Published);
            Assert.Equal(EventTypes.AlertValidated, published.DetailType);
        }

        [Fact]
        public async Task Handle_InvalidAlert_PublishesAlertRejected()
        {
            var detail = BuildDetail() with { Title = "" };
            var publisher = new FakeEventPublisher();
            var function = new Function(fixture.ConnectionString, publisher);

            var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, detail);
            await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

            var published = Assert.Single(publisher.Published);
            Assert.Equal(EventTypes.AlertRejected, published.DetailType);
        }

        [Fact]
        public async Task Handle_RedeliveredMessage_IsIgnored()
        {
            var detail = BuildDetail();
            var publisher = new FakeEventPublisher();
            var function = new Function(fixture.ConnectionString, publisher);

            var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.AlertReceived, detail);
            await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
            await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

            Assert.Single(publisher.Published);
        }
    }

    private static AlertReceivedDetail BuildDetail() => new(
        EventId: Guid.NewGuid(), OrganizationId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(), OccurredAtUtc: DateTimeOffset.UtcNow,
        AlertId: Guid.NewGuid(), IntegrationId: Guid.NewGuid(), ExternalId: "ext-1", Source: "datadog", Title: "High latency detected",
        Severity: Severity.High, TimestampUtc: DateTimeOffset.UtcNow, Environment: "production", Region: "us-east-1");
}
