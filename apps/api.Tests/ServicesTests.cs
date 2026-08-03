using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Common;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class ServicesTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_Get_Update_List_Delete_HappyPath()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Services Org");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services",
            new CreateServiceRequest("Payments API", "Handles payments", Guid.NewGuid(), ServiceEnvironment.Production));
        createResponse.EnsureSuccessStatusCode();
        var service = await createResponse.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options);

        var getResponse = await client.GetFromJsonAsync<ServiceResponse>(
            $"/api/v1/organizations/{org.Id}/services/{service!.Id}", Json.Options);
        Assert.Equal("Payments API", getResponse!.Name);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}",
            new UpdateServiceRequest("Payments API v2", "Updated", service.OwnerUserId, ServiceEnvironment.Staging, ServiceStatus.Degraded));
        updateResponse.EnsureSuccessStatusCode();
        var updated = await updateResponse.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options);
        Assert.Equal(ServiceStatus.Degraded, updated!.Status);

        var listResponse = await client.GetFromJsonAsync<PagedResult<ServiceResponse>>(
            $"/api/v1/organizations/{org.Id}/services", Json.Options);
        Assert.Contains(listResponse!.Items, s => s.Id == service.Id);

        var deleteResponse = await client.DeleteAsync($"/api/v1/organizations/{org.Id}/services/{service.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDelete = await client.GetAsync($"/api/v1/organizations/{org.Id}/services/{service.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotCreateService()
    {
        var owner = TestClientFactory.NewSub();
        var viewer = TestClientFactory.NewSub();
        var viewerEmail = $"{viewer}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Viewer Services Org");
        await InviteAndAcceptAsync(fixture, ownerClient, org.Id, viewer, viewerEmail, OrganizationRole.Viewer);

        var viewerClient = fixture.Factory.CreateClientFor(viewer, viewerEmail);
        var response = await viewerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services",
            new CreateServiceRequest("Should Fail", null, Guid.NewGuid(), ServiceEnvironment.Production));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_CannotAccessAnotherOrganizationsServices()
    {
        var ownerA = TestClientFactory.NewSub();
        var ownerB = TestClientFactory.NewSub();
        var clientA = fixture.Factory.CreateClientFor(ownerA);
        var clientB = fixture.Factory.CreateClientFor(ownerB);

        var orgA = await CreateOrganizationAsync(clientA, "Org A Services");
        var orgB = await CreateOrganizationAsync(clientB, "Org B Services");

        var response = await clientA.GetAsync($"/api/v1/organizations/{orgB.Id}/services");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AddDependency_RejectsSelfReferenceAndCycles()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Dependency Org");

        var serviceA = await CreateServiceAsync(client, org.Id, "A");
        var serviceB = await CreateServiceAsync(client, org.Id, "B");

        var selfResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{serviceA.Id}/dependencies/{serviceA.Id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, selfResponse.StatusCode);

        var linkResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{serviceA.Id}/dependencies/{serviceB.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, linkResponse.StatusCode);

        var cycleResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{serviceB.Id}/dependencies/{serviceA.Id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, cycleResponse.StatusCode);
    }

    [Fact]
    public async Task AlertRules_CreateListUpdateDelete()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Alert Rules Org");
        var service = await CreateServiceAsync(client, org.Id, "Auth Service");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/alert-rules",
            new CreateAlertRuleRequest("High error rate", "Fires on elevated 5xx rate", "error_rate > 5%", IncidentSeverity.High, true));
        createResponse.EnsureSuccessStatusCode();
        var rule = await createResponse.Content.ReadFromJsonAsync<AlertRuleResponse>(Json.Options);
        Assert.Equal(service.Id, rule!.ServiceId);

        var listResponse = await client.GetFromJsonAsync<List<AlertRuleResponse>>(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/alert-rules", Json.Options);
        Assert.Contains(listResponse!, r => r.Id == rule.Id);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/alert-rules/{rule.Id}",
            new UpdateAlertRuleRequest("High error rate", "Updated", "error_rate > 10%", IncidentSeverity.Critical, false));
        updateResponse.EnsureSuccessStatusCode();
        var updated = await updateResponse.Content.ReadFromJsonAsync<AlertRuleResponse>(Json.Options);
        Assert.Equal(IncidentSeverity.Critical, updated!.Severity);
        Assert.False(updated.IsEnabled);

        var deleteResponse = await client.DeleteAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/alert-rules/{rule.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Health_And_OpenIncidents_ReflectServiceState()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Health Org");
        var service = await CreateServiceAsync(client, org.Id, "Notification Service");

        var incidentResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new { Title = "Notifications delayed", Severity = IncidentSeverity.Critical, ServiceId = service.Id });
        incidentResponse.EnsureSuccessStatusCode();

        var health = await client.GetFromJsonAsync<ServiceHealthResponse>(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/health", Json.Options);
        Assert.Equal(1, health!.OpenIncidentCount);
        Assert.Equal(1, health.CriticalOpenIncidentCount);

        var openIncidents = await client.GetFromJsonAsync<List<IncidentResponse>>(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/incidents", Json.Options);
        Assert.Single(openIncidents!);
        Assert.Equal("Notifications delayed", openIncidents![0].Title);
    }

    private static async Task<ServiceResponse> CreateServiceAsync(HttpClient client, Guid orgId, string name)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/services",
            new CreateServiceRequest(name, null, Guid.NewGuid(), ServiceEnvironment.Production));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options))!;
    }
}
