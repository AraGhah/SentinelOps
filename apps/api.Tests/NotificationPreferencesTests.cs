using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Notifications;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class NotificationPreferencesTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Get_NoPreferenceConfigured_ReturnsDefaults()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Prefs Default Org");

        var preference = await client.GetFromJsonAsync<NotificationPreferenceResponse>(
            $"/api/v1/organizations/{org.Id}/notification-preferences/me", Json.Options);

        Assert.True(preference!.EmailEnabled);
        Assert.Null(preference.QuietHoursStartLocal);
        Assert.Null(preference.TimeZoneId);
    }

    [Fact]
    public async Task Put_SetsQuietHoursAndDisablesEmail_ThenGetReflectsIt()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Prefs Update Org");

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/notification-preferences/me",
            new UpdateNotificationPreferenceRequest(false, new TimeOnly(22, 0), new TimeOnly(7, 0), "America/New_York"));
        updateResponse.EnsureSuccessStatusCode();
        var updated = await updateResponse.Content.ReadFromJsonAsync<NotificationPreferenceResponse>(Json.Options);
        Assert.False(updated!.EmailEnabled);
        Assert.Equal(new TimeOnly(22, 0), updated.QuietHoursStartLocal);

        var refetched = await client.GetFromJsonAsync<NotificationPreferenceResponse>(
            $"/api/v1/organizations/{org.Id}/notification-preferences/me", Json.Options);
        Assert.False(refetched!.EmailEnabled);
        Assert.Equal("America/New_York", refetched.TimeZoneId);
    }

    [Fact]
    public async Task Put_QuietHoursStartWithoutEnd_IsRejected()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Prefs Invalid Org");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/notification-preferences/me",
            new UpdateNotificationPreferenceRequest(true, new TimeOnly(22, 0), null, "UTC"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_QuietHoursWithoutTimeZone_IsRejected()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Prefs Missing Tz Org");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/notification-preferences/me",
            new UpdateNotificationPreferenceRequest(true, new TimeOnly(22, 0), new TimeOnly(7, 0), null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
