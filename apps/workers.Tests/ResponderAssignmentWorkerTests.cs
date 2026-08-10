using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.ResponderAssignment;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class ResponderAssignmentWorkerTests(WorkerTestFixture fixture)
{
    private const string StateMachineArn = "arn:aws:states:us-east-1:123456789012:stateMachine:test-escalation";

    [Fact]
    public async Task Handle_EscalationPolicyWithTarget_AssignsResponderAndRequestsNotification()
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
            incident = TestData.NewIncident(db, orgId, service.Id);

            var policy = TestData.NewEscalationPolicy(db, orgId, service.Id);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, responderId);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new Function(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var detail = new IncidentCreatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, Guid.NewGuid(), incident.ServiceId, incident.Title, Severity.High);
        var message = SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Equal(2, publisher.Published.Count);
        Assert.Contains(publisher.Published, p => p.DetailType == EventTypes.IncidentUpdated);
        var notificationRequested = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);
        var notifyDetail = Assert.IsType<NotificationRequestedDetail>(notificationRequested.Detail);
        Assert.Equal(responderId, notifyDetail.RecipientUserId);

        // An applicable escalation policy exists, so the state machine must
        // have been started, seeded with that policy's first level.
        var started = Assert.Single(escalationStarter.Started);
        Assert.Equal(StateMachineArn, started.StateMachineArn);
        Assert.Contains(policyId.ToString(), started.InputJson);
        Assert.Contains("\"currentLevelOrder\":0", started.InputJson);
        Assert.Contains("\"ackTimeoutSeconds\":900", started.InputJson);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(responderId, reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Assigned, reloaded.Status);
        Assert.Equal(0, reloaded.CurrentEscalationLevel);
        Assert.Equal(1, await verifyDb.Notifications.CountAsync());
    }

    [Fact]
    public async Task Handle_OnlyBackupRotationCoversNow_AssignsBackupResponder()
    {
        var orgId = Guid.NewGuid();
        var backupResponderId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            incident = TestData.NewIncident(db, orgId, service.Id);

            var schedule = TestData.NewSchedule(db, orgId, service.Id);
            // A rotation spanning the whole week so this test isn't sensitive
            // to what day it actually runs on.
            for (var day = 0; day < 7; day++)
            {
                TestData.NewRotation(
                    db, orgId, schedule.Id, backupResponderId, day, new TimeOnly(0, 0), new TimeOnly(23, 59), isBackup: true);
            }

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new Function(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var detail = new IncidentCreatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, Guid.NewGuid(), incident.ServiceId, incident.Title, Severity.High);
        var message = SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        // No escalation policy exists for this service, so no execution
        // should have been started even though a responder was assigned.
        Assert.Empty(escalationStarter.Started);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(backupResponderId, reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Assigned, reloaded.Status);
        Assert.Null(reloaded.CurrentEscalationLevel);
    }

    [Fact]
    public async Task Handle_NoScheduleOrPolicy_LeavesIncidentUnassigned()
    {
        var orgId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            incident = TestData.NewIncident(db, orgId, service.Id);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var escalationStarter = new FakeEscalationStarter();
        var function = new Function(fixture.ConnectionString, publisher, escalationStarter, StateMachineArn);

        var detail = new IncidentCreatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, Guid.NewGuid(), incident.ServiceId, incident.Title, Severity.High);
        var message = SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);
        Assert.Empty(escalationStarter.Started);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Null(reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Triggered, reloaded.Status);
    }
}
