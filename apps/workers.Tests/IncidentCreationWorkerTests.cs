using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.IncidentCreation;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class IncidentCreationWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_UniqueAlert_CreatesIncidentAndPublishesIncidentCreated()
    {
        var orgId = Guid.NewGuid();
        Alert alert;
        Guid serviceId;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            serviceId = service.Id;
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            alert = TestData.NewAlert(db, orgId, integration.Id);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var request = new IncidentCreationRequest(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, alert.Id);
        var message = new SQSEvent.SQSMessage { Body = JsonSerializer.Serialize(request, EventJson.Options) };
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.IncidentCreated, published.DetailType);
        var createdDetail = Assert.IsType<IncidentCreatedDetail>(published.Detail);
        Assert.Equal(alert.Id, createdDetail.AlertId);
        Assert.Equal(serviceId, createdDetail.ServiceId);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloadedAlert = await verifyDb.Alerts.FirstAsync(a => a.Id == alert.Id);
        Assert.NotNull(reloadedAlert.IncidentId);
        var incident = await verifyDb.Incidents.FirstAsync(i => i.Id == reloadedAlert.IncidentId);
        Assert.Equal(IncidentStatus.Triggered, incident.Status);
    }

    [Fact]
    public async Task Handle_AlertAlreadyHasIncident_DoesNotCreateASecondOne()
    {
        var orgId = Guid.NewGuid();
        Alert alert;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            var existingIncident = TestData.NewIncident(db, orgId);
            alert = TestData.NewAlert(db, orgId, integration.Id, incidentId: existingIncident.Id);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var request = new IncidentCreationRequest(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, alert.Id);
        var message = new SQSEvent.SQSMessage { Body = JsonSerializer.Serialize(request, EventJson.Options) };
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        Assert.Equal(1, await verifyDb.Incidents.CountAsync());
    }

    [Fact]
    public async Task Handle_RedeliveredMessage_IsIgnored()
    {
        var orgId = Guid.NewGuid();
        Alert alert;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            alert = TestData.NewAlert(db, orgId, integration.Id);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher);

        var request = new IncidentCreationRequest(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, alert.Id);
        var message = new SQSEvent.SQSMessage { Body = JsonSerializer.Serialize(request, EventJson.Options) };
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Single(publisher.Published);
        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        Assert.Equal(1, await verifyDb.Incidents.CountAsync());
    }
}
