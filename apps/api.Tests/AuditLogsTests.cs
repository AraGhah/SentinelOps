using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Audit;
using SentinelOps.Api.Common;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class AuditLogsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task MutatingAction_ProducesQueryableAuditEntry()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Audit Org");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services",
            new CreateServiceRequest("Audited Service", null, Guid.NewGuid(), ServiceEnvironment.Production));
        createResponse.EnsureSuccessStatusCode();
        var service = await createResponse.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options);

        var auditLogs = await client.GetFromJsonAsync<PagedResult<AuditLogResponse>>(
            $"/api/v1/organizations/{org.Id}/audit-logs", Json.Options);

        Assert.Contains(auditLogs!.Items, a => a.Action == "service.created" && a.EntityId == service!.Id);
    }

    [Fact]
    public async Task Responder_CannotListAuditLogs()
    {
        var owner = TestClientFactory.NewSub();
        var responder = TestClientFactory.NewSub();
        var responderEmail = $"{responder}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Audit Perms Org");
        await InviteAndAcceptAsync(fixture, ownerClient, org.Id, responder, responderEmail, OrganizationRole.Responder);

        var responderClient = fixture.Factory.CreateClientFor(responder, responderEmail);
        var response = await responderClient.GetAsync($"/api/v1/organizations/{org.Id}/audit-logs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
