using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Escalation;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class EscalationRestartFunctionTests(WorkerTestFixture fixture)
{
    private const string StateMachineArn = "arn:aws:states:us-east-1:123456789012:stateMachine:test-escalation";

    private static IncidentUpdatedDetail ReopenedDetail(Guid orgId, Guid incidentId) => new(
        Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incidentId, "Status", "Resolved", "Reopened");

    [Fact]
    public async Task Handle_IncidentReopened_ReassignsResponderAndRestartsEscalation()
    {
        var orgId = Guid.NewGuid();
        var responderId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            incident = TestData.NewIncident(db, orgId, service.Id, status: IncidentStatus.Reopened);

            var policy = TestData.NewEscalationPolicy(db, orgId, service.Id);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, responderId);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new RestartFunction(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.IncidentUpdated, ReopenedDetail(orgId, incident.Id));
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var started = Assert.Single(escalationStarter.Started);
        Assert.Equal(StateMachineArn, started.StateMachineArn);
        Assert.Contains(policyId.ToString(), started.InputJson);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(responderId, reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Assigned, reloaded.Status);
        Assert.Equal(0, reloaded.CurrentEscalationLevel);
    }

    [Fact]
    public async Task Handle_UnrelatedFieldChange_IsIgnored()
    {
        var orgId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new RestartFunction(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var detail = new IncidentUpdatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, "AlertCount", "1", "2");
        var message = SqsEventFactory.Wrap(EventSources.DeduplicationWorker, EventTypes.IncidentUpdated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(escalationStarter.Started);
        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Null(reloaded.AssignedResponderUserId);
    }

    [Fact]
    public async Task Handle_RedeliveredMessage_IsIgnored()
    {
        var orgId = Guid.NewGuid();
        var responderId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            incident = TestData.NewIncident(db, orgId, service.Id, status: IncidentStatus.Reopened);

            var policy = TestData.NewEscalationPolicy(db, orgId, service.Id);
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, responderId);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new RestartFunction(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var message = SqsEventFactory.Wrap(EventSources.Api, EventTypes.IncidentUpdated, ReopenedDetail(orgId, incident.Id));
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Single(escalationStarter.Started);
    }
}
