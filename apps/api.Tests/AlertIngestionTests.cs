using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Ingestion;
using SentinelOps.Api.Integrations;
using SentinelOps.Api.Organizations;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class AlertIngestionTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Ingest_ValidSignedRequest_Returns202AndPublishesToQueue()
    {
        var (_, integration) = await CreateOrgAndIntegrationAsync("Ingestion Org");
        var publisher = fixture.Factory.Services.GetRequiredService<FakeAlertQueuePublisher>();
        var publishedBefore = publisher.Published.Count;

        var body = BuildAlertBody();
        var (timestamp, signature) = Sign(integration.SigningSecret, body);

        using var request = BuildRequest(integration.Integration.Id, integration.ApiKey, timestamp, signature, body);
        var response = await fixture.Factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<IngestAlertResponse>(Json.Options);
        Assert.Equal("accepted", result!.Status);
        Assert.NotEqual(Guid.Empty, result.AlertId);

        Assert.Equal(publishedBefore + 1, publisher.Published.Count);
        Assert.Contains(publisher.Published, m => m.AlertId == result.AlertId);
    }

    [Fact]
    public async Task Ingest_DuplicateIdempotencyKey_ReturnsSameAlertWithoutRequeueing()
    {
        var (_, integration) = await CreateOrgAndIntegrationAsync("Idempotent Org");
        var publisher = fixture.Factory.Services.GetRequiredService<FakeAlertQueuePublisher>();

        var body = BuildAlertBody();
        var idempotencyKey = Guid.NewGuid().ToString();

        var (timestamp1, signature1) = Sign(integration.SigningSecret, body);
        using var request1 = BuildRequest(
            integration.Integration.Id, integration.ApiKey, timestamp1, signature1, body, idempotencyKey);
        var response1 = await fixture.Factory.CreateClient().SendAsync(request1);
        response1.EnsureSuccessStatusCode();
        var result1 = await response1.Content.ReadFromJsonAsync<IngestAlertResponse>(Json.Options);

        var publishedAfterFirst = publisher.Published.Count;

        var (timestamp2, signature2) = Sign(integration.SigningSecret, body);
        using var request2 = BuildRequest(
            integration.Integration.Id, integration.ApiKey, timestamp2, signature2, body, idempotencyKey);
        var response2 = await fixture.Factory.CreateClient().SendAsync(request2);

        Assert.Equal(HttpStatusCode.Accepted, response2.StatusCode);
        var result2 = await response2.Content.ReadFromJsonAsync<IngestAlertResponse>(Json.Options);
        Assert.Equal("duplicate", result2!.Status);
        Assert.Equal(result1!.AlertId, result2.AlertId);
        Assert.Equal(publishedAfterFirst, publisher.Published.Count);
    }

    [Fact]
    public async Task Ingest_InvalidSignature_ReturnsUnauthorized()
    {
        var (_, integration) = await CreateOrgAndIntegrationAsync("Bad Signature Org");
        var body = BuildAlertBody();
        var (timestamp, _) = Sign(integration.SigningSecret, body);

        using var request = BuildRequest(integration.Integration.Id, integration.ApiKey, timestamp, "0".PadLeft(64, '0'), body);
        var response = await fixture.Factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_MissingApiKey_ReturnsUnauthorized()
    {
        var (_, integration) = await CreateOrgAndIntegrationAsync("Missing Key Org");
        var body = BuildAlertBody();
        var (timestamp, signature) = Sign(integration.SigningSecret, body);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/integrations/{integration.Integration.Id}/alerts")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add(AlertIngestionController.TimestampHeader, timestamp);
        httpRequest.Headers.Add(AlertIngestionController.SignatureHeader, signature);

        var response = await fixture.Factory.CreateClient().SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ingest_RevokedIntegration_ReturnsUnauthorized()
    {
        var (org, integration) = await CreateOrgAndIntegrationAsync("Revoked Org");
        var ownerClient = fixture.Factory.CreateClientFor(_ownerSub);
        await ownerClient.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{_serviceId}/integrations/{integration.Integration.Id}/revoke", null);

        var body = BuildAlertBody();
        var (timestamp, signature) = Sign(integration.SigningSecret, body);
        using var request = BuildRequest(integration.Integration.Id, integration.ApiKey, timestamp, signature, body);
        var response = await fixture.Factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestEvent_SendsSyntheticAlertThroughIngestion()
    {
        var (org, integration) = await CreateOrgAndIntegrationAsync("Test Event Org");
        var publisher = fixture.Factory.Services.GetRequiredService<FakeAlertQueuePublisher>();
        var publishedBefore = publisher.Published.Count;

        var ownerClient = fixture.Factory.CreateClientFor(_ownerSub);
        var response = await ownerClient.PostAsync(
            $"/api/v1/organizations/{org.Id}/services/{_serviceId}/integrations/{integration.Integration.Id}/test-event", null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TestEventResponse>(Json.Options);
        Assert.NotEqual(Guid.Empty, result!.AlertId);
        Assert.Equal(publishedBefore + 1, publisher.Published.Count);
    }

    private string _ownerSub = null!;
    private Guid _serviceId;

    private async Task<(SentinelOps.Api.Organizations.OrganizationResponse Org, IntegrationCreatedResponse Integration)> CreateOrgAndIntegrationAsync(string orgName)
    {
        _ownerSub = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(_ownerSub);
        var org = await CreateOrganizationAsync(client, orgName);

        var serviceResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services",
            new CreateServiceRequest("Payments", null, Guid.NewGuid(), ServiceEnvironment.Production));
        serviceResponse.EnsureSuccessStatusCode();
        var service = (await serviceResponse.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options))!;
        _serviceId = service.Id;

        var integrationResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services/{service.Id}/integrations",
            new CreateIntegrationRequest("Webhook", "Generic"));
        integrationResponse.EnsureSuccessStatusCode();
        var integration = (await integrationResponse.Content.ReadFromJsonAsync<IntegrationCreatedResponse>(Json.Options))!;

        return (org, integration);
    }

    private static string BuildAlertBody() => JsonSerializer.Serialize(new
    {
        externalId = $"ext-{Guid.NewGuid()}",
        source = "datadog",
        title = "High latency detected",
        description = "p99 latency above threshold",
        severity = (int)IncidentSeverity.High,
        timestampUtc = DateTimeOffset.UtcNow,
        environment = "production",
        region = "us-east-1",
        metadata = (string?)null,
    });

    private static (string Timestamp, string Signature) Sign(string signingSecret, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var key = Encoding.UTF8.GetBytes(signingSecret);
        var message = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, message)).ToLowerInvariant();
        return (timestamp, signature);
    }

    private static HttpRequestMessage BuildRequest(
        Guid integrationId, string apiKey, string timestamp, string signature, string body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrations/{integrationId}/alerts")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyAuthenticationHandler.ApiKeyHeader, apiKey);
        request.Headers.Add(AlertIngestionController.TimestampHeader, timestamp);
        request.Headers.Add(AlertIngestionController.SignatureHeader, signature);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(AlertIngestionController.IdempotencyKeyHeader, idempotencyKey);
        }

        return request;
    }
}
