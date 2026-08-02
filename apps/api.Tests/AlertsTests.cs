using System.Net.Http.Json;
using SentinelOps.Api.Alerts;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Integrations;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class AlertsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task LinkIncident_IncrementsAlertCount_UnlinkDecrements()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Alerts Org");

        var service = await CreateServiceAsync(client, org.Id);
        var integrationCreated = await CreateIntegrationAsync(client, org.Id, service.Id);
        var incident = await CreateIncidentAsync(client, org.Id);

        var createAlertResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/alerts",
            new CreateAlertRequest(
                integrationCreated.Integration.Id, "ext-1", "Datadog", "High CPU", null, IncidentSeverity.High,
                DateTimeOffset.UtcNow, "production", "us-east-1", null));
        createAlertResponse.EnsureSuccessStatusCode();
        var alert = await createAlertResponse.Content.ReadFromJsonAsync<AlertResponse>(Json.Options);

        var linkResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/alerts/{alert!.Id}/link-incident", new LinkAlertIncidentRequest(incident.Id));
        linkResponse.EnsureSuccessStatusCode();

        var incidentAfterLink = await client.GetFromJsonAsync<IncidentResponse>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}", Json.Options);
        Assert.Equal(1, incidentAfterLink!.AlertCount);

        var unlinkResponse = await client.PutAsync($"/api/v1/organizations/{org.Id}/alerts/{alert.Id}/unlink-incident", null);
        unlinkResponse.EnsureSuccessStatusCode();

        var incidentAfterUnlink = await client.GetFromJsonAsync<IncidentResponse>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}", Json.Options);
        Assert.Equal(0, incidentAfterUnlink!.AlertCount);
    }

    private static async Task<ServiceResponse> CreateServiceAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/services",
            new CreateServiceRequest("Payments", null, Guid.NewGuid(), ServiceEnvironment.Production));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options))!;
    }

    private static async Task<IntegrationCreatedResponse> CreateIntegrationAsync(HttpClient client, Guid orgId, Guid serviceId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/services/{serviceId}/integrations",
            new CreateIntegrationRequest("Datadog", "Datadog"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IntegrationCreatedResponse>(Json.Options))!;
    }

    private static async Task<IncidentResponse> CreateIncidentAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/incidents",
            new CreateIncidentRequest("Incident", null, IncidentSeverity.Medium, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;
    }
}
