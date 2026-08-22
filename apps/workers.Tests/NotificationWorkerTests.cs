using Amazon.Lambda.SQSEvents;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Domain;
using SentinelOps.Events;
using SentinelOps.Workers.Notification;

namespace SentinelOps.Workers.Tests;

public class StubNotificationChannel(bool succeed, bool isTransient = false, string? failureReason = null) : INotificationChannel
{
    public List<(string RecipientEmail, string Subject, string HtmlBody, string TextBody)> Sent { get; } = [];

    public Task<NotificationSendResult> SendAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken ct)
    {
        Sent.Add((recipientEmail, subject, htmlBody, textBody));
        return Task.FromResult(new NotificationSendResult(succeed, succeed ? false : isTransient, succeed ? null : failureReason));
    }
}

[Collection("Workers")]
public class NotificationWorkerTests(WorkerTestFixture fixture)
{
    [Fact]
    public async Task Handle_SuccessfulSend_MarksDeliveredAndPublishesNotificationDelivered()
    {
        var (orgId, notification, recipient) = await SeedNotificationAsync();

        var publisher = new FakeEventPublisher();
        var channel = new StubNotificationChannel(succeed: true);
        var function = new Function(fixture.ConnectionString, publisher, channel);

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.NotificationDelivered, published.DetailType);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal(recipient.Email, sent.RecipientEmail);
        Assert.Contains("assigned", sent.Subject, StringComparison.OrdinalIgnoreCase);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Delivered, reloaded.Status);
        Assert.NotNull(reloaded.DeliveredAtUtc);
    }

    [Fact]
    public async Task Handle_PermanentFailure_MarksFailedAndPublishesNotificationFailed()
    {
        var (orgId, notification, _) = await SeedNotificationAsync();

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher, new StubNotificationChannel(succeed: false, isTransient: false, "message rejected"));

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.NotificationFailed, published.DetailType);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Failed, reloaded.Status);
        Assert.Equal("message rejected", reloaded.FailureReason);
    }

    [Fact]
    public async Task Handle_TransientFailure_ThrowsForRedeliveryWithoutMarkingFailed()
    {
        var (orgId, notification, _) = await SeedNotificationAsync();

        var publisher = new FakeEventPublisher();
        var function = new Function(fixture.ConnectionString, publisher, new StubNotificationChannel(succeed: false, isTransient: true, "throttled"));

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);

        var response = await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        var failure = Assert.Single(response.BatchItemFailures);
        Assert.Equal(message.MessageId, failure.ItemIdentifier);
        Assert.Empty(publisher.Published);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Requested, reloaded.Status);
    }

    [Fact]
    public async Task Handle_QuietHoursActive_SuppressesWithoutSending()
    {
        var (orgId, notification, recipient) = await SeedNotificationAsync();

        // A window covering the whole day in UTC, so "now" is always inside it.
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            db.NotificationPreferences.Add(new NotificationPreference
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = recipient.Id,
                EmailEnabled = true,
                QuietHoursStartLocal = new TimeOnly(0, 0),
                QuietHoursEndLocal = new TimeOnly(23, 59),
                TimeZoneId = "UTC",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var channel = new StubNotificationChannel(succeed: true);
        var function = new Function(fixture.ConnectionString, publisher, channel);

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(publisher.Published);
        Assert.Empty(channel.Sent);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Suppressed, reloaded.Status);
    }

    [Fact]
    public async Task Handle_EmailChannelDisabled_SuppressesWithoutSending()
    {
        var (orgId, notification, recipient) = await SeedNotificationAsync();

        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            db.NotificationPreferences.Add(new NotificationPreference
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                UserId = recipient.Id,
                EmailEnabled = false,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var channel = new StubNotificationChannel(succeed: true);
        var function = new Function(fixture.ConnectionString, publisher, channel);

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(channel.Sent);
        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Suppressed, reloaded.Status);
    }

    [Fact]
    public async Task Handle_RecipientNoLongerExists_MarksFailedPermanently()
    {
        var orgId = Guid.NewGuid();
        SentinelOps.Api.Domain.Notification notification;
        await using (var db = fixture.CreateOrgScopedDb(orgId))
        {
            var org = TestData.NewOrganization(db);
            org.Id = orgId;
            var service = TestData.NewService(db, orgId);
            var incident = TestData.NewIncident(db, orgId, service.Id, IncidentStatus.Assigned);

            notification = new SentinelOps.Api.Domain.Notification
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                IncidentId = incident.Id,
                RecipientUserId = Guid.NewGuid(), // no matching User row
                Channel = "email",
                Kind = NotificationKind.IncidentAssigned,
                Status = NotificationStatus.Requested,
                RequestedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
        }

        var publisher = new FakeEventPublisher();
        var channel = new StubNotificationChannel(succeed: true);
        var function = new Function(fixture.ConnectionString, publisher, channel);

        var detail = new NotificationRequestedDetail(
            Guid.NewGuid(), orgId, Guid.NewGuid(), DateTimeOffset.UtcNow, notification.Id, notification.IncidentId, notification.RecipientUserId, notification.Channel);
        var message = SqsEventFactory.Wrap(EventSources.ResponderAssignmentWorker, EventTypes.NotificationRequested, detail);
        await function.FunctionHandler(new SQSEvent { Records = [message] }, SqsEventFactory.Context());

        Assert.Empty(channel.Sent);
        var published = Assert.Single(publisher.Published);
        Assert.Equal(EventTypes.NotificationFailed, published.DetailType);

        await using var verifyDb = fixture.CreateOrgScopedDb(orgId);
        var reloaded = await verifyDb.Notifications.FirstAsync(n => n.Id == notification.Id);
        Assert.Equal(NotificationStatus.Failed, reloaded.Status);
    }

    private async Task<(Guid OrgId, SentinelOps.Api.Domain.Notification Notification, User Recipient)> SeedNotificationAsync()
    {
        var orgId = Guid.NewGuid();
        await using var db = fixture.CreateOrgScopedDb(orgId);
        var org = TestData.NewOrganization(db);
        org.Id = orgId;
        var service = TestData.NewService(db, orgId);
        var incident = TestData.NewIncident(db, orgId, service.Id, IncidentStatus.Assigned);
        var recipient = TestData.NewUser(db);

        var notification = new SentinelOps.Api.Domain.Notification
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incident.Id,
            RecipientUserId = recipient.Id,
            Channel = "email",
            Kind = NotificationKind.IncidentAssigned,
            Status = NotificationStatus.Requested,
            RequestedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Notifications.Add(notification);

        await db.SaveChangesAsync();
        return (orgId, notification, recipient);
    }
}
