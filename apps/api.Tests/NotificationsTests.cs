using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SentinelOps.Api.Common;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Notifications;
using SentinelOps.Events;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class NotificationsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task ResolvingAnAssignedIncident_CreatesAResolutionNotification_AndPublishesNotificationRequested()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Resolution Notif Org");

        var responderSub = TestClientFactory.NewSub();
        var responderId = await InviteAndAcceptUserIdAsync(
            fixture, client, org.Id, responderSub, $"{responderSub}@test.local", OrganizationRole.Responder);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Payments down", null, IncidentSeverity.Critical, null, responderId));
        createResponse.EnsureSuccessStatusCode();
        var incident = (await createResponse.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;

        var publisher = fixture.Factory.Services.GetRequiredService<FakeEventPublisher>();

        var resolveResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, "fixed"));
        resolveResponse.EnsureSuccessStatusCode();

        Assert.Contains(publisher.Published, p => p.DetailType == EventTypes.NotificationRequested);

        var notifications = await client.GetFromJsonAsync<PagedResult<NotificationResponse>>(
            $"/api/v1/organizations/{org.Id}/notifications?incidentId={incident.Id}", Json.Options);
        var resolutionNotification = Assert.Single(notifications!.Items, n => n.Kind == nameof(NotificationKind.IncidentResolved));
        Assert.Equal(responderId, resolutionNotification.RecipientUserId);
        Assert.Equal(nameof(NotificationStatus.Requested), resolutionNotification.Status);
    }

    [Fact]
    public async Task ResolvingAnUnassignedIncident_CreatesNoNotification()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Unassigned Resolution Org");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Unassigned incident", null, IncidentSeverity.Low, null, null));
        createResponse.EnsureSuccessStatusCode();
        var incident = (await createResponse.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;

        var resolveResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, null));
        resolveResponse.EnsureSuccessStatusCode();

        var notifications = await client.GetFromJsonAsync<PagedResult<NotificationResponse>>(
            $"/api/v1/organizations/{org.Id}/notifications?incidentId={incident.Id}", Json.Options);
        Assert.Empty(notifications!.Items);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Notif Filter Org");

        var response = await client.GetAsync(
            $"/api/v1/organizations/{org.Id}/notifications?status={nameof(NotificationStatus.Delivered)}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<NotificationResponse>>(Json.Options);
        Assert.Empty(result!.Items);
    }

    [Fact]
    public async Task List_UnknownStatus_ReturnsBadRequest()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Notif Bad Filter Org");

        var response = await client.GetAsync($"/api/v1/organizations/{org.Id}/notifications?status=NotARealStatus");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
