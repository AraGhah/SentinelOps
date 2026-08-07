using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Notification;

namespace SentinelOps.Workers.Tests;

public class StubNotificationChannel(bool succeed, string? failureReason = null) : INotificationChannel
{
    public Task<NotificationSendResult> SendAsync(Guid recipientUserId, string channel, string message, CancellationToken ct) =>
        Task.FromResult(new NotificationSendResult(succeed, succeed ? null : failureReason));
}

[Collection("Workers")]
public class NotificationWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_SuccessfulSend_MarksDeliveredAndPublishesNotificationDelivered()
    {
        var (orgId, notification) = await SeedNotificationAsync();

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher, new StubNotificationChannel(succeed: true));

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.NotificationDelivered, published.DetailType);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Delivered, reloaded.Status);
        Assert.NotNull(reloaded.DeliveredAtUtc);
    }

    [Fact]
    public async Task Handle_FailedSend_MarksFailedAndPublishesNotificationFailed()
    {
        var (orgId, notification) = await SeedNotificationAsync();

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher, new StubNotificationChannel(succeed: false, "channel unreachable"));

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.NotificationFailed, published.DetailType);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Failed, reloaded.Status);
        Assert.Equal("channel unreachable", reloaded.FailureReason);
    }

    private async Task<(Guid OrgId, SentinelOps.Api.Domain.Notification Notification)> SeedNotificationAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = fixture.CreateOrgScopedDb(orgId);
        var org = TestData.NewOrganization(db);
        org.Id = orgId;
        var service = TestData.NewService(db, orgId);
        var incident = TestData.NewIncident(db, orgId, service.Id, IncidentStatus.Assigned);

        var notification = new SentinelOps.Api.Domain.Notification
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incident.Id,
            RecipientUserId = Guid.NewGuid(),
            Channel = "email",
            Status = NotificationStatus.Requested,
            RequestedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(notification);

        await db.SaveChangesAsync();
        return (orgId, notification);
    }
}
