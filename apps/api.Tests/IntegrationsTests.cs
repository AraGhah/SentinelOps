using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Integrations;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class IntegrationsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_ReturnsPlaintextKeyOnce_AndRotateChangesHash()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Integrations Org");
        var service = await CreateServiceAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations",
            new CreateIntegrationRequest("Prod Webhook", "Generic"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<IntegrationCreatedResponse>(Json.Options);

        Assert.StartsWith("sops_", created!.ApiKey);
        Assert.Equal(created.ApiKey[^4..], created.Integration.ApiKeyLastFour);

        var rotateResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations/{created.Integration.Id}/rotate-key", null);
        rotateResponse.EnsureSuccessStatusCode();
        var rotated = await rotateResponse.Content.ReadFromJsonAsync<IntegrationCreatedResponse>(Json.Options);

        Assert.NotEqual(created.ApiKey, rotated!.ApiKey);
    }

    [Fact]
    public async Task Revoke_SetsStatusToRevoked()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Revoke Org");
        var service = await CreateServiceAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations",
            new CreateIntegrationRequest("Webhook", "Generic"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<IntegrationCreatedResponse>(Json.Options);

        var revokeResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations/{created!.Integration.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var getResponse = await client.GetFromJsonAsync<IntegrationResponse>(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations/{created.Integration.Id}", Json.Options);
        Assert.Equal(IntegrationStatus.Revoked, getResponse!.Status);
    }

    [Fact]
    public async Task Responder_CannotCreateIntegration()
    {
        var owner = TestClientFactory.NewSub();
        var responder = TestClientFactory.NewSub();
        var responderEmail = $"{responder}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Responder Integrations Org");
        var service = await CreateServiceAsync(ownerClient, org.Id);
        await InviteAndAcceptAsync(fixture, ownerClient, org.Id, responder, responderEmail, OrganizationRole.Responder);

        var responderClient = fixture.Factory.CreateClientFor(responder, responderEmail);
        var response = await responderClient.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations",
            new CreateIntegrationRequest("Should Fail", "Generic"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<ServiceResponse> CreateServiceAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/services",
            new CreateServiceRequest("Payments", null, Guid.NewGuid(), ServiceEnvironment.Production));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options))!;
    }
}
