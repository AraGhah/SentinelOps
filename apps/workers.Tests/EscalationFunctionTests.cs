using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Escalation;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class EscalationFunctionTests(WorkerTestFixture fixture)
{
    [Theory]
    [InlineData(IncidentStatus.Triggered, false)]
    [InlineData(IncidentStatus.Assigned, false)]
    [InlineData(IncidentStatus.Acknowledged, true)]
    [InlineData(IncidentStatus.Investigating, true)]
    [InlineData(IncidentStatus.Monitoring, true)]
    [InlineData(IncidentStatus.Resolved, true)]
    public async Task CheckStatus_ReflectsWhetherEscalationShouldStop(IncidentStatus status, bool expectedAcknowledgedOrResolved)
    {
        var orgId = Guid.NewGuid();
        Incident incident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: status);
            await db.SaveChangesAsync();
        }

        var function = new EscalationFunction(fixture.ConnectionString, new FakeEventPublisher());
        var input = new EscalationStateInput("CheckStatus", orgId, incident.Id, Guid.NewGuid(), Guid.NewGuid(), 0, 900, false);

        var result = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.Equal(expectedAcknowledgedOrResolved, result.AcknowledgedOrResolved);
    }

    [Fact]
    public async Task AdvanceLevel_NextLevelExists_NotifiesItAndPublishesEscalated()
    {
        var orgId = Guid.NewGuid();
        var level2ResponderId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: IncidentStatus.Assigned);

            var policy = TestData.NewEscalationPolicy(db, orgId);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, Guid.NewGuid());
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 1, ackTimeoutMinutes: 30, level2ResponderId);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("AdvanceLevel", orgId, incident.Id, Guid.NewGuid(), policyId, 0, 900, false);

        var result = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.False(result.Stop);
        Assert.Equal(1, result.CurrentLevelOrder);
        Assert.Equal(1800, result.AckTimeoutSeconds);

        var escalated = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.IncidentEscalated);
        var escalatedDetail = Assert.IsType<IncidentEscalatedDetail>(escalated.Detail);
        Assert.Equal(0, escalatedDetail.FromLevel);
        Assert.Equal(1, escalatedDetail.ToLevel);
        Assert.Equal(level2ResponderId, escalatedDetail.AssignedUserId);

        var notified = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);
        Assert.Equal(level2ResponderId, ((NotificationRequestedDetail)notified.Detail).RecipientUserId);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Incidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(1, reloaded.CurrentEscalationLevel);
        Assert.Equal(1, await verifyDb.Notifications.CountAsync());
    }

    [Fact]
    public async Task AdvanceLevel_NoMoreLevels_NotifiesFallbackAdministrator()
    {
        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: IncidentStatus.Assigned);

            var policy = TestData.NewEscalationPolicy(db, orgId, fallbackAdministratorUserId: adminId);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, Guid.NewGuid());

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("AdvanceLevel", orgId, incident.Id, Guid.NewGuid(), policyId, 0, 900, false);

        var result = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.False(result.Stop);
        Assert.True(result.FallbackNotified);

        var notified = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);
        Assert.Equal(adminId, ((NotificationRequestedDetail)notified.Detail).RecipientUserId);
    }

    [Fact]
    public async Task AdvanceLevel_NoMoreLevelsAndFallbackAlreadyNotified_Stops()
    {
        var orgId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: IncidentStatus.Assigned);

            var policy = TestData.NewEscalationPolicy(db, orgId, fallbackAdministratorUserId: Guid.NewGuid());
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, Guid.NewGuid());

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("AdvanceLevel", orgId, incident.Id, Guid.NewGuid(), policyId, 0, 900, FallbackNotified: true);

        var result = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.True(result.Stop);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AdvanceLevel_NoMoreLevelsAndNoFallbackConfigured_Stops()
    {
        var orgId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: IncidentStatus.Assigned);

            var policy = TestData.NewEscalationPolicy(db, orgId);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, Guid.NewGuid());

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("AdvanceLevel", orgId, incident.Id, Guid.NewGuid(), policyId, 0, 900, false);

        var result = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.True(result.Stop);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AdvanceLevel_RetriedForSameLevel_DoesNotDoubleNotify()
    {
        // Step Functions can retry the same task after a transient error; must not double-notify.
        var orgId = Guid.NewGuid();
        var level2ResponderId = Guid.NewGuid();
        Incident incident;
        Guid policyId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            incident = TestData.NewIncident(db, orgId, status: IncidentStatus.Assigned);

            var policy = TestData.NewEscalationPolicy(db, orgId);
            policyId = policy.Id;
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 0, ackTimeoutMinutes: 15, Guid.NewGuid());
            TestData.NewEscalationLevel(db, orgId, policy.Id, order: 1, ackTimeoutMinutes: 30, level2ResponderId);

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("AdvanceLevel", orgId, incident.Id, Guid.NewGuid(), policyId, 0, 900, false);

        var first = await function.FunctionHandler(input, SqsEventFactory.Context());
        var second = await function.FunctionHandler(input, SqsEventFactory.Context());

        Assert.Equal(first.CurrentLevelOrder, second.CurrentLevelOrder);
        Assert.Equal(first.AckTimeoutSeconds, second.AckTimeoutSeconds);
        Assert.Single(publisher.Published, p => p.DetailType == EventTypes.IncidentEscalated);
        Assert.Single(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        Assert.Equal(1, await verifyDb.Notifications.CountAsync());
    }

    [Fact]
    public async Task RecordFailure_PublishesEscalationWorkflowFailedMarker()
    {
        var orgId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var publisher = new FakeEventPublisher();
        var function = new EscalationFunction(fixture.ConnectionString, publisher);
        var input = new EscalationStateInput("RecordFailure", orgId, incidentId, Guid.NewGuid(), Guid.NewGuid(), 0, 900, false);

        await function.FunctionHandler(input, SqsEventFactory.Context());

        var updated = Assert.Single(publisher.Published, p => p.DetailType == EventTypes.IncidentUpdated);
        var detail = Assert.IsType<IncidentUpdatedDetail>(updated.Detail);
        Assert.Equal("EscalationWorkflow", detail.Field);
        Assert.Equal("Failed", detail.NewValue);
    }
}
