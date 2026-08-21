using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Organizations;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class OrganizationsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task UpdateSettings_RejectsUnknownTimeZoneId()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Bad Tz Settings Org");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/settings",
            new UpdateOrganizationSettingsRequest("Not/AZone", null, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unknown time zone id.", body);
    }

    [Fact]
    public async Task UpdateSettings_AcceptsValidTimeZoneId()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Good Tz Settings Org");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/settings",
            new UpdateOrganizationSettingsRequest("America/New_York", null, false));

        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<OrganizationSettingsResponse>(Json.Options);
        Assert.Equal("America/New_York", settings!.TimeZone);
    }
}
