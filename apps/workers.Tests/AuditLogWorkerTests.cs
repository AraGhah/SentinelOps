using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Events;
using SentinelOps.Workers.AuditLog;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class AuditLogWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_AnyEvent_RecordsOneAuditLogEntry()
    {
        var orgId = Guid.NewGuid();
        var function = new Function(fixture.ConnectionString);

        var detail = new AlertRejectedDetail(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "Alert timestamp is too old.");
        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertRejected, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        await using var db = fixture.CreateOrgScopedDb(orgId);
        var row = await db.AuditLogs.SingleAsync(a => a.EntityId == detail.EventId);
        Assert.Equal(EventTypes.AlertRejected, row.Action);
        Assert.Equal(orgId, row.OrganizationId);
        Assert.Null(row.ActorUserId);
    }

    [Fact]
    public async Task Handle_RedeliveredMessage_DoesNotDuplicateEntry()
    {
        var orgId = Guid.NewGuid();
        var function = new Function(fixture.ConnectionString);

        var detail = new AlertRejectedDetail(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "Alert is missing a title.");
        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertRejected, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        await using var db = fixture.CreateOrgScopedDb(orgId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == detail.EventId));
    }
}
