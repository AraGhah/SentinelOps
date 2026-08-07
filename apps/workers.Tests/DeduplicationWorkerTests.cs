using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Deduplication;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class DeduplicationWorkerTests(WorkerTestFixture fixture)
{
    private const string IncidentCreationQueueUrl = "https://sqs.test/incident-creation";

    [Fact]
    public async Task Handle_MatchingOpenIncidentExists_AttachesAndPublishesIncidentUpdated()
    {
        var orgId = Guid.NewGuid();
        Alert newAlert;
        Incident existingIncident;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);

            existingIncident = TestData.NewIncident(db, orgId);
            TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-1", incidentId: existingIncident.Id);
            newAlert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-1");

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl);

        var detail = new AlertValidatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow,
            newAlert.Id, "ext-1", "datadog", "High latency detected", Severity.High, "production", null);
        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.IncidentUpdated, published.DetailType);
        Assert.Empty(queueSender.Sent);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloadedAlert = await verifyDb.Alerts.FirstAsync(a => a.Id == newAlert.Id);
        var reloadedIncident = await verifyDb.Incidents.FirstAsync(i => i.Id == existingIncident.Id);
        Assert.Equal(existingIncident.Id, reloadedAlert.IncidentId);
        Assert.Equal(2, reloadedIncident.AlertCount);
    }

    [Fact]
    public async Task Handle_NoMatchingIncident_SendsDirectlyToIncidentCreationQueue()
    {
        var orgId = Guid.NewGuid();
        Alert alert;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            alert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-unique");
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl);

        var detail = new AlertValidatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow,
            alert.Id, "ext-unique", "datadog", "High latency detected", Severity.High, "production", null);
        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);
        var sent = Assert.Single(queueSender.Sent);
        Assert.Equal(IncidentCreationQueueUrl, sent.QueueUrl);
        Assert.Contains(alert.Id.ToString(), sent.Body);
    }

    [Fact]
    public async Task Handle_ResolvedIncidentAlreadyExists_TreatedAsNoMatch()
    {
        var orgId = Guid.NewGuid();
        Alert newAlert;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);

            var resolvedIncident = TestData.NewIncident(db, orgId, status: IncidentStatus.Resolved);
            TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-resolved", incidentId: resolvedIncident.Id);
            newAlert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-resolved");

            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl);

        var detail = new AlertValidatedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow,
            newAlert.Id, "ext-resolved", "datadog", "High latency detected", Severity.High, "production", null);
        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);
        Assert.Single(queueSender.Sent);
    }
}
