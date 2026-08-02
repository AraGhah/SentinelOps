using System.Net;
using System.Net.Http.Json;
using SentinelOps.Api.Attachments;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class AttachmentsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_List_Delete_HappyPath()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Attachments Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments",
            new CreateAttachmentRequest("screenshot.png", "image/png", 2048, "orgs/x/incidents/y/screenshot.png"));
        createResponse.EnsureSuccessStatusCode();
        var attachment = await createResponse.Content.ReadFromJsonAsync<AttachmentResponse>(Json.Options);

        var listResponse = await client.GetFromJsonAsync<List<AttachmentResponse>>(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments", Json.Options);
        Assert.Contains(listResponse!, a => a.Id == attachment!.Id);

        var deleteResponse = await client.DeleteAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/attachments/{attachment!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    private static async Task<IncidentResponse> CreateIncidentAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/incidents",
            new CreateIncidentRequest("Incident for attachment", null, IncidentSeverity.Medium, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;
    }
}
