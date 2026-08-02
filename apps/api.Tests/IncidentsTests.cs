using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class IncidentsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_Get_Update_List_Delete_HappyPath()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Incidents Org");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Database down", "Primary DB unreachable", IncidentSeverity.Critical, null, null));
        createResponse.EnsureSuccessStatusCode();
        var incident = await createResponse.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options);
        Assert.Equal(IncidentStatus.Triggered, incident!.Status);

        var getResponse = await client.GetFromJsonAsync<IncidentResponse>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}", Json.Options);
        Assert.Equal("Database down", getResponse!.Title);

        var deleteResponse = await client.DeleteAsync($"/api/v1/organizations/{org.Id}/incidents/{incident.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task StatusTransition_WritesHistory_AndBlocksSkippingReopen()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Status Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var ackResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Acknowledged, "picked up"));
        ackResponse.EnsureSuccessStatusCode();

        var resolveResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, "fixed"));
        resolveResponse.EnsureSuccessStatusCode();

        var blockedResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Investigating, null));
        Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);

        var reopenResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Reopened, "recurred"));
        reopenResponse.EnsureSuccessStatusCode();

        var history = await client.GetFromJsonAsync<List<IncidentStatusHistoryResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status-history", Json.Options);
        Assert.Equal(3, history!.Count);
        Assert.Equal(IncidentStatus.Reopened, history[^1].ToStatus);
    }

    [Fact]
    public async Task RelatedIncidents_LinkIsBidirectional()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Related Org");
        var incidentA = await CreateIncidentAsync(client, org.Id);
        var incidentB = await CreateIncidentAsync(client, org.Id);

        var linkResponse = await client.PostAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incidentA.Id}/related/{incidentB.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, linkResponse.StatusCode);

        var relatedToA = await client.GetFromJsonAsync<List<Guid>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incidentA.Id}/related", Json.Options);
        var relatedToB = await client.GetFromJsonAsync<List<Guid>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incidentB.Id}/related", Json.Options);

        Assert.Contains(incidentB.Id, relatedToA!);
        Assert.Contains(incidentA.Id, relatedToB!);
    }

    [Fact]
    public async Task Viewer_CannotCreateIncident()
    {
        var owner = TestClientFactory.NewSub();
        var viewer = TestClientFactory.NewSub();
        var viewerEmail = $"{viewer}@test.local";

        var ownerClient = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(ownerClient, "Viewer Incidents Org");
        await InviteAndAcceptAsync(fixture, ownerClient, org.Id, viewer, viewerEmail, OrganizationRole.Viewer);

        var viewerClient = fixture.Factory.CreateClientFor(viewer, viewerEmail);
        var response = await viewerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Should fail", null, IncidentSeverity.Low, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_CannotAccessAnotherOrganizationsIncidents()
    {
        var ownerA = TestClientFactory.NewSub();
        var ownerB = TestClientFactory.NewSub();
        var clientA = fixture.Factory.CreateClientFor(ownerA);
        var clientB = fixture.Factory.CreateClientFor(ownerB);

        var orgA = await CreateOrganizationAsync(clientA, "Org A Incidents");
        var orgB = await CreateOrganizationAsync(clientB, "Org B Incidents");

        var response = await clientA.GetAsync($"/api/v1/organizations/{orgB.Id}/incidents");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
