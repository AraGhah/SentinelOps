using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Deduplication;
using SentinelOps.Workers.Shared;

namespace SentinelOps.Workers.Tests;

[Collection("Workers")]
public class DeduplicationWorkerTests(WorkerTestFixture fixture)
{
    private const string IncidentCreationQueueUrl = "https://sqs.test/incident-creation";

    private static AlertValidatedDetail Detail(Guid orgId, Guid alertId, string title = "High latency detected") =>
        new(Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow,
            alertId, "ext-1", "datadog", title, Severity.High, "production", null);

    [Fact]
    public async Task Handle_FirstAlertForFingerprint_SendsDirectlyToIncidentCreationQueue()
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
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl, new FakeFingerprintStore());

        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, alert.Id));
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);
        var sent = Assert.Single(queueSender.Sent);
        Assert.Equal(IncidentCreationQueueUrl, sent.QueueUrl);
        Assert.Contains(alert.Id.ToString(), sent.Body);
    }

    [Fact]
    public async Task Handle_SecondAlertForSameFingerprint_AttachesToIncidentAndPublishesIncidentUpdated()
    {
        var orgId = Guid.NewGuid();
        Alert firstAlert, secondAlert;
        Guid incidentId;
        var fingerprintStore = new FakeFingerprintStore();
        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();

        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            firstAlert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-1");
            secondAlert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-2");
            await db.SaveChangesAsync();
        }

        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl, fingerprintStore);

        // First alert claims the fingerprint and hands off to incident-creation.
        var firstMessage = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, firstAlert.Id));
        await function.FunctionHandler(new SQSEvent { Records = [firstMessage] }, SqsEventFactory.Context());

        // Simulate incident-creation worker having finished.
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var incident = TestData.NewIncident(db, orgId);
            incidentId = incident.Id;
            await db.SaveChangesAsync();
            var reloadedFirstAlert = await db.Alerts.FirstAsync(a => a.Id == firstAlert.Id);
            reloadedFirstAlert.IncidentId = incidentId;
            await db.SaveChangesAsync();
        }
        var fingerprint = queueSender.Sent.Single().Body;
        var fingerprintValue = System.Text.Json.JsonDocument.Parse(fingerprint).RootElement.GetProperty("fingerprint").GetString()!;
        await fingerprintStore.SetIncidentIdAsync(fingerprintValue, incidentId, TimeSpan.FromHours(1), CancellationToken.None);

        // Second alert, same fingerprint, attaches.
        var secondMessage = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, secondAlert.Id));
        await function.FunctionHandler(new SQSEvent { Records = [secondMessage] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.IncidentUpdated, published.DetailType);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloadedSecondAlert = await verifyDb.Alerts.FirstAsync(a => a.Id == secondAlert.Id);
        var reloadedIncident = await verifyDb.Incidents.FirstAsync(i => i.Id == incidentId);
        Assert.Equal(incidentId, reloadedSecondAlert.IncidentId);
        Assert.Equal(2, reloadedIncident.AlertCount);
    }

    [Fact]
    public async Task Handle_DuplicateAlert_ReopensResolvedIncident()
    {
        var orgId = Guid.NewGuid();
        Alert newAlert;
        Guid incidentId;
        Guid serviceId;
        var fingerprintStore = new FakeFingerprintStore();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            serviceId = service.Id;
            var integration = TestData.NewIntegration(db, orgId, service.Id);

            var resolvedIncident = TestData.NewIncident(db, orgId, status: IncidentStatus.Resolved);
            resolvedIncident.ResolvedAtUtc = DateTimeOffset.UtcNow;
            incidentId = resolvedIncident.Id;
            newAlert = TestData.NewAlert(db, orgId, integration.Id, externalId: "ext-new");
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl, fingerprintStore);

        var detail = Detail(orgId, newAlert.Id);
        var errorCode = AlertFingerprint.ExtractErrorCode(null);
        var fingerprint = AlertFingerprint.Compute(orgId, detail.Title, errorCode, serviceId, detail.Environment);
        await fingerprintStore.TouchAsync(fingerprint, TimeSpan.FromHours(1), CancellationToken.None);
        await fingerprintStore.SetIncidentIdAsync(fingerprint, incidentId, TimeSpan.FromHours(1), CancellationToken.None);

        var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(queueSender.Sent);
        Assert.Contains(publisher.Published, p => p.DetailType == EventTypes.IncidentUpdated
            && ((IncidentUpdatedDetail)p.Detail).Field == "Status" && ((IncidentUpdatedDetail)p.Detail).NewValue == "Reopened");

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var incident = await verifyDb.Incidents.FirstAsync(i => i.Id == incidentId);
        Assert.Equal(IncidentStatus.Reopened, incident.Status);
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Equal(2, incident.AlertCount);
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicateWhileWinnerStillPending_ThrowsForRedelivery()
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

        var fingerprintStore = new FakeFingerprintStore();
        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl, fingerprintStore);

        // First delivery claims the fingerprint; nobody has created the incident yet.
        var firstMessage = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, alert.Id));
        await function.FunctionHandler(new SQSEvent { Records = [firstMessage] }, SqsEventFactory.Context());

        // Concurrent duplicate arrives before the winner's incident-creation worker has run.
        var secondMessage = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, alert.Id));
        await Assert.ThrowsAsync<FingerprintPendingException>(
            () => function.FunctionHandler(new SQSEvent { Records = [secondMessage] }, SqsEventFactory.Context()));

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Handle_100SimultaneousIdenticalAlerts_ExactlyOneIsTreatedAsUnique()
    {
        var orgId = Guid.NewGuid();
        var alertIds = new List<Guid>();
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var integration = TestData.NewIntegration(db, orgId, service.Id);
            for (var i = 0; i < 100; i++)
            {
                alertIds.Add(TestData.NewAlert(db, orgId, integration.Id, externalId: $"ext-{i}").Id);
            }
            await db.SaveChangesAsync();
        }

        var fingerprintStore = new FakeFingerprintStore();
        var publisher = new FakeEventPublisher();
        var queueSender = new FakeQueueSender();

        // All alerts hash to the same fingerprint despite distinct rows; one shared Function
        // instance mimics a warm Lambda handling concurrent invocations.
        var function = new Function(fixture.ConnectionString, publisher, queueSender, IncidentCreationQueueUrl, fingerprintStore);

        var tasks = alertIds.Select(async alertId =>
        {
            var message = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgId, alertId));
            try
            {
                await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());
                return true;
            }
            catch (FingerprintPendingException)
            {
                return false;
            }
        });
        var results = await Task.WhenAll(tasks);

        // Exactly one alert wins and hands off; every other concurrent arrival sees Pending and throws.
        Assert.Single(queueSender.Sent);
        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(99, results.Count(succeeded => !succeeded));
    }

    [Fact]
    public async Task Handle_SameFingerprintDifferentOrganizations_DoNotShareAnIncident()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        Alert alertA, alertB;

        await using (var db = fixture.CreateOrgScopedDb(orgA))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgA;
            var service = TestData.NewService(db, orgA);
            var integration = TestData.NewIntegration(db, orgA, service.Id);
            alertA = TestData.NewAlert(db, orgA, integration.Id, externalId: "ext-a");
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateOrgScopedDb(orgB))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgB;
            var service = TestData.NewService(db, orgB);
            var integration = TestData.NewIntegration(db, orgB, service.Id);
            alertB = TestData.NewAlert(db, orgB, integration.Id, externalId: "ext-b");
            await db.SaveChangesAsync();
        }

        var fingerprintStore = new FakeFingerprintStore();
        var publisher = new FakeEventPublisher();
        var queueSenderA = new FakeQueueSender();
        var queueSenderB = new FakeQueueSender();
        var functionA = new Function(fixture.ConnectionString, publisher, queueSenderA, IncidentCreationQueueUrl, fingerprintStore);
        var functionB = new Function(fixture.ConnectionString, publisher, queueSenderB, IncidentCreationQueueUrl, fingerprintStore);

        // Identical title/environment but different orgs; fingerprint folds in OrganizationId.
        var messageA = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgA, alertA.Id));
        var messageB = SqsEventFactory.Wrap(EventSources.AlertValidationWorker, EventTypes.AlertValidated, Detail(orgB, alertB.Id));

        await functionA.FunctionHandler(new SQSEvent { Records = [messageA] }, SqsEventFactory.Context());
        await functionB.FunctionHandler(new SQSEvent { Records = [messageB] }, SqsEventFactory.Context());

        // Both treated as unique — hand off independently, neither attaches to the other's incident.
        Assert.Single(queueSenderA.Sent);
        Assert.Single(queueSenderB.Sent);
    }
}
