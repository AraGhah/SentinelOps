using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Events;
using SentinelOps.Workers.Analytics;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class AnalyticsWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_AnyEvent_RecordsOneAnalyticsRowPerEvent()
    {
        var orgId = Guid.NewGuid();
        var function = new Function(fixture.ConnectionString);

        var detail = new IncidentAcknowledgedDetail(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid());
        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.IncidentAcknowledged, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        await using var db = fixture.CreateOrgScopedDb(orgId);
        var row = await db.AnalyticsEvents.IgnoreQueryFilters().SingleAsync(e => e.SourceEventId == detail.EventId);
        Assert.Equal(EventTypes.IncidentAcknowledged, row.EventType);
        Assert.Equal(orgId, row.OrganizationId);
    }

    [Fact]
    public async Task Handle_RedeliveredMessage_DoesNotDuplicateRow()
    {
        var orgId = Guid.NewGuid();
        var function = new Function(fixture.ConnectionString);

        var detail = new IncidentResolvedDetail(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid());
        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.IncidentResolved, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        await using var db = fixture.CreateOrgScopedDb(orgId);
        Assert.Equal(1, await db.AnalyticsEvents.IgnoreQueryFilters().CountAsync(e => e.SourceEventId == detail.EventId));
    }
}
