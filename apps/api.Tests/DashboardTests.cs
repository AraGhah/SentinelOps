using System.Net.Http.Json;
using SentinelOps.Api.Dashboard;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Incidents;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class DashboardTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task GetSummary_NoData_ReturnsZeroesAndNulls()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Empty Dashboard Org");

        var summary = await client.GetFromJsonAsync<DashboardSummaryResponse>(
            $"/api/v1/organizations/{org.Id}/dashboard/summary", Json.Options);

        Assert.Equal(0, summary!.ActiveIncidentCount);
        Assert.Empty(summary.RecentlyResolvedIncidents);
        Assert.Null(summary.MeanAcknowledgementTimeSeconds);
        Assert.Null(summary.MeanResolutionTimeSeconds);
        Assert.Equal(0, summary.AlertVolumeLast24Hours);
        Assert.Empty(summary.ServiceHealth);
    }

    [Fact]
    public async Task GetSummary_CountsActiveIncidentsBySeverity_AndExcludesResolvedFromActiveCount()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Active Incidents Org");

        await CreateIncidentAsync(client, org.Id, "Critical one", IncidentSeverity.Critical);
        await CreateIncidentAsync(client, org.Id, "High one", IncidentSeverity.High);
        var resolved = await CreateIncidentAsync(client, org.Id, "Will resolve", IncidentSeverity.Low);
        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{resolved.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, null));

        var summary = await client.GetFromJsonAsync<DashboardSummaryResponse>(
            $"/api/v1/organizations/{org.Id}/dashboard/summary", Json.Options);

        Assert.Equal(2, summary!.ActiveIncidentCount);
        Assert.Equal(1, summary.ActiveIncidentsBySeverity[nameof(IncidentSeverity.Critical)]);
        Assert.Equal(1, summary.ActiveIncidentsBySeverity[nameof(IncidentSeverity.High)]);
        Assert.Equal(0, summary.ActiveIncidentsBySeverity[nameof(IncidentSeverity.Low)]);
        var resolvedSummary = Assert.Single(summary.RecentlyResolvedIncidents);
        Assert.Equal(resolved.Id, resolvedSummary.Id);
    }

    [Fact]
    public async Task GetSummary_ComputesMeanAcknowledgementAndResolutionTime()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "MTTA Org");
        var incident = await CreateIncidentAsync(client, org.Id, "Slow to ack", IncidentSeverity.Medium);

        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Acknowledged, null));
        await client.PutAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents/{incident.Id}/status",
            new UpdateIncidentStatusRequest(IncidentStatus.Resolved, null));

        var summary = await client.GetFromJsonAsync<DashboardSummaryResponse>(
            $"/api/v1/organizations/{org.Id}/dashboard/summary", Json.Options);

        Assert.NotNull(summary!.MeanAcknowledgementTimeSeconds);
        Assert.True(summary.MeanAcknowledgementTimeSeconds >= 0);
        Assert.NotNull(summary.MeanResolutionTimeSeconds);
        Assert.True(summary.MeanResolutionTimeSeconds >= 0);
    }

    [Fact]
    public async Task GetSummary_IncludesServiceHealthForEveryServiceInTheOrg()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Service Health Org");

        var serviceResponse = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/services",
            new CreateServiceRequest("Payments", null, Guid.NewGuid(), ServiceEnvironment.Production));
        serviceResponse.EnsureSuccessStatusCode();
        var service = (await serviceResponse.Content.ReadFromJsonAsync<ServiceResponse>(Json.Options))!;

        await client.PostAsJsonAsync(
            $"/api/v1/organizations/{org.Id}/incidents",
            new CreateIncidentRequest("Payments down", null, IncidentSeverity.Critical, service.Id, null));

        var summary = await client.GetFromJsonAsync<DashboardSummaryResponse>(
            $"/api/v1/organizations/{org.Id}/dashboard/summary", Json.Options);

        var health = Assert.Single(summary!.ServiceHealth, h => h.ServiceId == service.Id);
        Assert.Equal(1, health.OpenIncidentCount);
        Assert.Equal(1, health.CriticalOpenIncidentCount);
    }

    private static async Task<IncidentResponse> CreateIncidentAsync(HttpClient client, Guid orgId, string title, IncidentSeverity severity)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{orgId}/incidents", new CreateIncidentRequest(title, null, severity, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentResponse>(Json.Options))!;
    }
}
