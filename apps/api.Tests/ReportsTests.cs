using System.Net.Http.Json;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Reports;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class ReportsTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Create_Update_ReviewStatus_HappyPath()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Reports Org");
        var incident = await CreateIncidentAsync(client, org.Id);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/reports",
            new CreateReportRequest("Summary text", "Some customers affected", "Bad deploy", "Alert fired", "Rolled back", "Add tests", "None"));
        createResponse.EnsureSuccessStatusCode();
        var report = await createResponse.Content.ReadFromJsonAsync<ReportResponse>(Json.Options);
        Assert.Equal(ReportReviewStatus.Draft, report!.ReviewStatus);

        var reviewResponse = await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/reports/{report.Id}/review-status",
            new UpdateReportReviewStatusRequest(ReportReviewStatus.Approved));
        reviewResponse.EnsureSuccessStatusCode();
        var updated = await reviewResponse.Content.ReadFromJsonAsync<ReportResponse>(Json.Options);

        Assert.Equal(ReportReviewStatus.Approved, updated!.ReviewStatus);
    }

    private static async Task<IncidentResponse> CreateIncidentAsync(HttpClient client, Guid orgId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/incidents",
            new CreateIncidentRequest("Incident for report", null, IncidentSeverity.Medium, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;
    }
}
