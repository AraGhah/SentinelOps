using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.ResponderAssignment;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class ResponderAssignmentWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_EscalationPolicyWithTarget_AssignsResponderAndRequestsNotification()
    {
        var orgId = Guid.NewGuid();
        var responderId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            incident = TestData.NewIncident(db, orgId, service.Id);

            var policy = new EscalationPolicy { Id = Guid.NewGuid(), OrganizationId = orgId, Name = "Default", ServiceId = service.Id };
            var level = new EscalationLevel { Id = Guid.NewGuid(), OrganizationId = orgId, EscalationPolicyId = policy.Id, Order = 0, AckTimeoutMinutes = 15 };
            var target = new EscalationLevelTarget { Id = Guid.NewGuid(), OrganizationId = orgId, EscalationLevelId = level.Id, UserId = responderId };
            db.EscalationPolicies.Add(policy);
            db.EscalationLevels.Add(level);
            db.EscalationLevelTargets.Add(target);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var detail = new IncidentCreatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, Guid.NewGuid(), incident.ServiceId, incident.Title, Severity.High);
        var message = SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Equal(2, publisher.Published.Count);
        Assert.Contains(publisher.Published, p => p.DetailType == EventTypes.IncidentUpdated);
        var notificationRequested = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);
        var notifyDetail = Assert.IsType<NotificationRequestedDetail>(notificationRequested.Detail);
        Assert.Equal(responderId, notifyDetail.RecipientUserId);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(responderId, reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Assigned, reloaded.Status);
        Assert.Equal(1, await verifyDb.Notifications.CountAsync());
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
        var function = new Function(fixture.ConnectionString, publisher);

        var detail = new IncidentCreatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, incident.Id, Guid.NewGuid(), incident.ServiceId, incident.Title, Severity.High);
        var message = SqsEventFactory.Wrap(EventSources.IncidentCreationWorker, EventTypes.IncidentCreated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Null(reloaded.AssignedResponderUserId);
        Assert.Equal(IncidentStatus.Triggered, reloaded.Status);
    }
}
