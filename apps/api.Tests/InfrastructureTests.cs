using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SentinelOps.Api.Common;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Services;
using static SentinelOps.Api.Tests.OrgTestHelpers;

namespace SentinelOps.Api.Tests;

[Collection("Api")]
public class InfrastructureTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task List_ReturnsPagedResultShape_AndSecondPageReturnsRemainder()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Pagination Org");

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/organizations/{org.Id}/services",
                new CreateServiceRequest($"Service {i}", null, Guid.NewGuid(), ServiceEnvironment.Production));
            response.EnsureSuccessStatusCode();
        }

        var firstPage = await client.GetFromJsonAsync<PagedResult<ServiceResponse>>(
            $"/api/v1/organizations/{org.Id}/services?page=1&pageSize=3", Json.Options);
        var secondPage = await client.GetFromJsonAsync<PagedResult<ServiceResponse>>(
            $"/api/v1/organizations/{org.Id}/services?page=2&pageSize=3", Json.Options);

        Assert.Equal(3, firstPage!.Items.Count);
        Assert.Equal(5, firstPage.TotalCount);
        Assert.Equal(2, secondPage!.Items.Count);
        Assert.Empty(firstPage.Items.Select(i => i.Id).Intersect(secondPage.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Create_WithMissingRequiredField_Returns400ValidationProblem()
    {
        var owner = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(owner);
        var org = await CreateOrganizationAsync(client, "Validation Org");

        var response = await client.PostAsJsonAsync($"/api/v1/organizations/{org.Id}/services", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task ApiRateLimitPolicy_Returns429AfterLimitExceeded()
    {
        var sub = TestClientFactory.NewSub();
        var client = fixture.Factory.CreateClientFor(sub);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 105; i++)
        {
            last = await client.GetAsync("/api/v1/organizations");
            if (last.StatusCode == HttpStatusCode.TooManyRequests) break;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }
}
